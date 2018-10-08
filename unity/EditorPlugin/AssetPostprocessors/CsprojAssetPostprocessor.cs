using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using JetBrains.Annotations;
using JetBrains.Rider.Unity.Editor.NonUnity;
using JetBrains.Util;
using JetBrains.Util.Logging;
using UnityEditor;
using UnityEngine;

namespace JetBrains.Rider.Unity.Editor.AssetPostprocessors
{
  public class CsprojAssetPostprocessor : AssetPostprocessor
  {
    private static readonly ILog ourLogger = Log.GetLog<CsprojAssetPostprocessor>();

    // Note that this does not affect the order in which postprocessors are evaluated. Order of execution is undefined.
    // https://github.com/Unity-Technologies/UnityCsReference/blob/2018.2/Editor/Mono/AssetPostprocessor.cs#L152
    public override int GetPostprocessOrder()
    {
      return 10;
    }

    // This method is new for 2018.1. It allows multiple processors to modify the contents of the generated .csproj in
    // memory, and Unity will only write to disk if it's different to the existing file. It's safe for pre-2018.1 as it
    // simply won't get called https://github.com/Unity-Technologies/UnityCsReference/blob/2018.1/Editor/Mono/AssetPostprocessor.cs#L76
    // ReSharper disable once InconsistentNaming
    [UsedImplicitly]
    public static string OnGeneratedCSProject(string path, string contents)
    {
      if (!PluginEntryPoint.Enabled)
        return contents;

      try
      {
        ourLogger.Verbose("Post-processing {0} (in memory)", path);
        var doc = XDocument.Parse(contents);
        if (UpgradeProjectFile(path, doc))
        {
          ourLogger.Verbose("Post-processed with changes {0} (in memory)", path);
          using (var sw = new Utf8StringWriter())
          {
            doc.Save(sw);
            return sw.ToString(); // https://github.com/JetBrains/resharper-unity/issues/727
          }
        }

        ourLogger.Verbose("Post-processed with NO changes {0}", path);
        return contents;
      }
      catch (Exception e)
      {
        // unhandled exception kills editor
        Debug.LogError(e);
        return contents;
      }
    }

    // This method is for pre-2018.1, and is called after the file has been written to disk
    public static void OnGeneratedCSProjectFiles()
    {
      if (!PluginEntryPoint.Enabled || UnityUtils.UnityVersion >= new Version(2018, 1))
        return;

      try
      {
        // get only csproj files, which are mentioned in sln
        var lines = SlnAssetPostprocessor.GetCsprojLinesInSln();
        var currentDirectory = Directory.GetCurrentDirectory();
        var projectFiles = Directory.GetFiles(currentDirectory, "*.csproj")
          .Where(csprojFile => lines.Any(line => line.Contains("\"" + Path.GetFileName(csprojFile) + "\""))).ToArray();

        foreach (var file in projectFiles)
        {
          UpgradeProjectFile(file);
        }
      }
      catch (Exception e)
      {
        // unhandled exception kills editor
        Debug.LogError(e);
      }
    }

    private static void UpgradeProjectFile(string projectFile)
    {
      ourLogger.Verbose("Post-processing {0}", projectFile);
      XDocument doc;
      try
      {
        doc = XDocument.Load(projectFile);
      }
      catch (Exception)
      {
        ourLogger.Verbose("Failed to Load {0}", projectFile);
        return;
      }

      if (UpgradeProjectFile(projectFile, doc))
      {
        ourLogger.Verbose("Post-processed with changes {0}ss", projectFile);
        doc.Save(projectFile);
        return;
      }

      ourLogger.Verbose("Post-processed with NO changes {0}", projectFile);
    }

    private static bool UpgradeProjectFile(string projectFile, XDocument doc)
    {
      var projectContentElement = doc.Root;
      XNamespace xmlns = projectContentElement.Name.NamespaceName; // do not use var

      var changed = FixTargetFrameworkVersion(projectContentElement, xmlns);
      changed |= FixUnityEngineReference(projectContentElement, xmlns); // shouldn't be needed in Unity 2018.2
      changed |= FixSystemXml(projectContentElement, xmlns);
      changed |= SetLangVersion(projectContentElement, xmlns);
      changed |= SetProjectFlavour(projectContentElement, xmlns);
      changed |= SetManuallyDefinedCompilerSettings(projectFile, projectContentElement, xmlns);
      changed |= TrySetHintPathsForSystemAssemblies(projectContentElement, xmlns);
      changed |= AddMicrosoftCSharpReference(projectContentElement, xmlns);
      changed |= SetXCodeDllReference("UnityEditor.iOS.Extensions.Xcode.dll", projectContentElement, xmlns);
      changed |= SetXCodeDllReference("UnityEditor.iOS.Extensions.Common.dll", projectContentElement, xmlns);
      changed |= SetDisableHandlePackageFileConflicts(projectContentElement, xmlns);
      changed |= AvoidGetReferenceAssemblyPathsCall(projectContentElement, xmlns);
      changed |= SetGenerateTargetFrameworkAttribute(projectContentElement, xmlns);
      
      return changed;
    }

    private static bool TrySetHintPathsForSystemAssemblies(XElement projectContentElement, XNamespace xmlns)
    {
      var elementsToUpdate = projectContentElement
        .Elements(xmlns+"ItemGroup")
        .Elements(xmlns+"Reference")
        .Where(a => a.Attribute("Include") != null)
        .Where(b => b.HasAttributes && b.Elements(xmlns + "HintPath").SingleOrDefault() == null)
        .ToArray();
      foreach (var element in elementsToUpdate)
      {
        var referenceName = element.Attribute("Include").Value + ".dll";
        var hintPath = GetHintPath(referenceName);
        AddCustomReference(referenceName, projectContentElement, xmlns, hintPath);
      }

      if (elementsToUpdate.Any())
      {
        elementsToUpdate.Remove();
        return true;
      }

      return false;
    }

    private static bool SetGenerateTargetFrameworkAttribute(XElement projectContentElement, XNamespace xmlns)
    {
      //https://youtrack.jetbrains.com/issue/RIDER-17390
      
      if (UnityUtils.ScriptingRuntime > 0)  
        return false;
      
      return SetOrUpdateProperty(projectContentElement, xmlns, "GenerateTargetFrameworkAttribute", existing => "false");
    }

    private static bool AddMicrosoftCSharpReference (XElement projectContentElement, XNamespace xmlns)
    {
      string referenceName = "Microsoft.CSharp.dll";
      
      if (UnityUtils.ScriptingRuntime == 0)
        return false;

      if (ourApiCompatibilityLevel != apiCompatibilityLevelNet46)
        return false;
      
      var hintPath = GetHintPath(referenceName);
      AddCustomReference(referenceName, projectContentElement, xmlns, hintPath);
      return true;
    }
    
    private static bool AvoidGetReferenceAssemblyPathsCall(XElement projectContentElement, XNamespace xmlns)
    {
      // Starting with Unity 2018, dotnet target pack is not required
      if (UnityUtils.UnityVersion < new Version(2018, 1))
        return false;
      
      // Set _TargetFrameworkDirectories and _FullFrameworkReferenceAssemblyPaths to something to avoid GetReferenceAssemblyPaths task being called
      return SetOrUpdateProperty(projectContentElement, xmlns, "_TargetFrameworkDirectories", 
               existing => string.IsNullOrEmpty(existing) ? "non_empty_path_generated_by_rider_editor_plugin" : existing)
             &&
             SetOrUpdateProperty(projectContentElement, xmlns, "_FullFrameworkReferenceAssemblyPaths",
               existing => string.IsNullOrEmpty(existing) ? "non_empty_path_generated_by_rider_editor_plugin" : existing);
    }

    private static bool SetDisableHandlePackageFileConflicts(XElement projectContentElement, XNamespace xmlns)
    {
      // https://developercommunity.visualstudio.com/content/problem/138986/1550-preview-2-breaks-scriptsharp-compilation.html
      // RIDER-18316 Rider fails to resolve mscorlib

      return SetOrUpdateProperty(projectContentElement, xmlns, "DisableHandlePackageFileConflicts", existing => "true");
    }

    private static bool FixSystemXml(XElement projectContentElement, XNamespace xmlns)
    {
      var el = projectContentElement
        .Elements(xmlns+"ItemGroup")
        .Elements(xmlns+"Reference")
        .FirstOrDefault(a => a.Attribute("Include") !=null && a.Attribute("Include").Value=="System.XML");
      if (el != null)
      {
        el.Attribute("Include").Value = "System.Xml";
        return true;
      }

      return false;
    }

    private const string UNITY_UNSAFE_KEYWORD = "-unsafe";
    private const string UNITY_DEFINE_KEYWORD = "-define:";
    private static readonly string PROJECT_MANUAL_CONFIG_ROSLYN_FILE_PATH = Path.GetFullPath("Assets/csc.rsp");
    private static readonly string PROJECT_MANUAL_CONFIG_FILE_PATH = Path.GetFullPath("Assets/mcs.rsp");
    private static readonly string PLAYER_PROJECT_MANUAL_CONFIG_FILE_PATH = Path.GetFullPath("Assets/smcs.rsp");
    private static readonly string EDITOR_PROJECT_MANUAL_CONFIG_FILE_PATH = Path.GetFullPath("Assets/gmcs.rsp");
    private static readonly int ourApiCompatibilityLevel = GetApiCompatibilityLevel();
    private const int apiCompatibilityLevelNet46 = 3;

    private static bool SetManuallyDefinedCompilerSettings(string projectFile, XElement projectContentElement, XNamespace xmlns)
    {
      var configPath = GetConfigPath(projectFile);
      return ApplyManualCompilerSettings(configPath, projectContentElement, xmlns);
    }

    [CanBeNull]
    private static string GetConfigPath(string projectFile)
    {
      // First choice - prefer csc.rsp if it exists
      if (File.Exists(PROJECT_MANUAL_CONFIG_ROSLYN_FILE_PATH))
        return PROJECT_MANUAL_CONFIG_ROSLYN_FILE_PATH;

      // Second choice - prefer mcs.rsp if it exists
      if (File.Exists(PROJECT_MANUAL_CONFIG_FILE_PATH))
        return PROJECT_MANUAL_CONFIG_FILE_PATH;

      var filename = Path.GetFileName(projectFile);
      if (filename == "Assembly-CSharp.csproj")
        return PLAYER_PROJECT_MANUAL_CONFIG_FILE_PATH;
      if (filename == "Assembly-CSharp-Editor.csproj")
        return EDITOR_PROJECT_MANUAL_CONFIG_FILE_PATH;

      return null;
    }

    private static bool ApplyManualCompilerSettings([CanBeNull] string configFilePath, XElement projectContentElement, XNamespace xmlns)
    {
      if (string.IsNullOrEmpty(configFilePath) || !File.Exists(configFilePath)) 
        return false;
      
      var configText = File.ReadAllText(configFilePath);
      var isUnity20171OrLater = UnityUtils.UnityVersion >= new Version(2017, 1);

      var changed = false;
      
      // Unity always sets AllowUnsafeBlocks in 2017.1+
      // Strictly necessary to compile unsafe code
      // https://github.com/Unity-Technologies/UnityCsReference/blob/2017.1/Editor/Mono/VisualStudioIntegration/SolutionSynchronizationSettings.cs#L119
      if (configText.Contains(UNITY_UNSAFE_KEYWORD) && !isUnity20171OrLater)
      {
        changed |= ApplyAllowUnsafeBlocks(projectContentElement, xmlns);
      }

      // Unity natively handles this in 2017.1+
      // https://github.com/Unity-Technologies/UnityCsReference/blob/33cbfe062d795667c39e16777230e790fcd4b28b/Editor/Mono/VisualStudioIntegration/SolutionSynchronizer.cs#L191
      // Also note that we don't support the short "-d" form. Neither does Unity
      if (configText.Contains(UNITY_DEFINE_KEYWORD) && !isUnity20171OrLater)
      {
        // defines could be
        // 1) -define:DEFINE1,DEFINE2
        // 2) -define:DEFINE1;DEFINE2
        // 3) -define:DEFINE1 -define:DEFINE2
        // 4) -define:DEFINE1,DEFINE2;DEFINE3
        // tested on "-define:DEF1;DEF2 -define:DEF3,DEF4;DEFFFF \n -define:DEF5"
        // result: DEF1, DEF2, DEF3, DEF4, DEFFFF, DEF5

        var definesList = new List<string>();
        var compileFlags = configText.Split(' ', '\n');
        foreach (var flag in compileFlags)
        {
          var f = flag.Trim();
          if (f.Contains(UNITY_DEFINE_KEYWORD))
          {
            var defineEndPos = f.IndexOf(UNITY_DEFINE_KEYWORD) + UNITY_DEFINE_KEYWORD.Length;
            var definesSubString = f.Substring(defineEndPos, f.Length - defineEndPos);
            definesSubString = definesSubString.Replace(";", ",");
            definesList.AddRange(definesSubString.Split(','));
          }
        }

        changed |= ApplyCustomDefines(definesList.ToArray(), projectContentElement, xmlns);
      }

      // Note that this doesn't handle the long version "-reference:"
      if (configText.Contains(UNITY_REFERENCE_KEYWORD))
      {
        changed |= ApplyManualCompilerSettingsReferences(projectContentElement, xmlns, configText);
      }

      return changed;
    }

    private static bool ApplyCustomDefines(string[] customDefines, XElement projectContentElement, XNamespace xmlns)
    {
      var definesString = string.Join(";", customDefines);

      var defineConstants = projectContentElement
        .Elements(xmlns+"PropertyGroup")
        .Elements(xmlns+"DefineConstants")
        .FirstOrDefault(definesConsts=> !string.IsNullOrEmpty(definesConsts.Value));

      defineConstants?.SetValue(defineConstants.Value + ";" + definesString);
      return true;
    }

    private static bool ApplyAllowUnsafeBlocks(XElement projectContentElement, XNamespace xmlns)
    {
      projectContentElement.AddFirst(
        new XElement(xmlns + "PropertyGroup", new XElement(xmlns + "AllowUnsafeBlocks", true)));
      return true;
    }

    private static bool SetXCodeDllReference(string name, XElement projectContentElement, XNamespace xmlns)
    {
      var unityAppBaseFolder = Path.GetFullPath(EditorApplication.applicationContentsPath);
      var xcodeDllPath = Path.Combine(unityAppBaseFolder, Path.Combine("Data/PlaybackEngines/iOSSupport", name));
      if (!File.Exists(xcodeDllPath))
        xcodeDllPath = Path.Combine(unityAppBaseFolder, Path.Combine("PlaybackEngines/iOSSupport", name));

      if (!File.Exists(xcodeDllPath))
        return false;

      AddCustomReference(Path.GetFileNameWithoutExtension(xcodeDllPath), projectContentElement, xmlns, xcodeDllPath);
      return true;
    }

    private static bool FixUnityEngineReference(XElement projectContentElement, XNamespace xmlns)
    {
      // Handled natively by Unity 2018.2+
      if (UnityUtils.UnityVersion >= new Version(2018, 2))
        return false;
      
      var unityAppBaseFolder = Path.GetDirectoryName(EditorApplication.applicationPath);
      if (string.IsNullOrEmpty(unityAppBaseFolder))
      {
        ourLogger.Verbose("FixUnityEngineReference. unityAppBaseFolder IsNullOrEmpty");
        return false;
      }

      var el = projectContentElement
        .Elements(xmlns+"ItemGroup")
        .Elements(xmlns+"Reference")
        .FirstOrDefault(a => a.Attribute("Include") !=null && a.Attribute("Include").Value=="UnityEngine");
      var hintPath = el?.Elements(xmlns + "HintPath").FirstOrDefault();
      if (hintPath == null)
        return false;

      var oldUnityEngineDllFileInfo = new FileInfo(hintPath.Value);
      var unityEngineDir = new DirectoryInfo(Path.Combine(oldUnityEngineDllFileInfo.Directory.FullName, "UnityEngine"));
      if (!unityEngineDir.Exists)
        return false;

      var newDllPath = Path.Combine(unityEngineDir.FullName, "UnityEngine.dll");
      if (!File.Exists(newDllPath))
        return false;

      hintPath.SetValue(newDllPath);

      var files = unityEngineDir.GetFiles("*.dll");
      foreach (var file in files)
      {
        AddCustomReference(Path.GetFileNameWithoutExtension(file.Name), projectContentElement, xmlns, file.FullName);
      }

      return true;
    }

    private const string UNITY_REFERENCE_KEYWORD = "-r:";

    private static bool ApplyManualCompilerSettingsReferences(XElement projectContentElement, XNamespace xmlns, string configText)
    {
      var referenceList = new List<string>();
      var compileFlags = configText.Split(' ', '\n');
      foreach (var flag in compileFlags)
      {
        var f = flag.Trim();
        if (f.Contains(UNITY_REFERENCE_KEYWORD))
        {
          var defineEndPos = f.IndexOf(UNITY_REFERENCE_KEYWORD) + UNITY_REFERENCE_KEYWORD.Length;
          var definesSubString = f.Substring(defineEndPos, f.Length - defineEndPos);
          definesSubString = definesSubString.Replace(";", ",");
          referenceList.AddRange(definesSubString.Split(','));
        }
      }

      foreach (var reference in referenceList)
      {
        var name = reference.Trim().TrimStart('"').TrimEnd('"');
        var nameFileInfo = new FileInfo(name);
        if (nameFileInfo.Extension.ToLower() != ".dll")
          name += ".dll"; // RIDER-15093

        string hintPath;
        if (!nameFileInfo.Exists)
          hintPath = GetHintPath(name);
        else
          hintPath = nameFileInfo.FullName;
        AddCustomReference(name, projectContentElement, xmlns, hintPath);
      }

      return true;
    }

    [CanBeNull]
    private static string GetHintPath(string name)
    {
      // Without hintpath non-Unity MSBuild will resolve assembly from dotnetframework targets path
      string hintPath = null;
      
      var unityAppBaseFolder = Path.GetFullPath(EditorApplication.applicationContentsPath);
      var monoDir = new DirectoryInfo(Path.Combine(unityAppBaseFolder, "MonoBleedingEdge/lib/mono"));
      if (!monoDir.Exists)
        monoDir = new DirectoryInfo(Path.Combine(unityAppBaseFolder, "Data/MonoBleedingEdge/lib/mono"));

      var mask = "4.*";
      if (UnityUtils.ScriptingRuntime == 0)
        mask = "2.*";
      
      var apiDir = monoDir.GetDirectories(mask).LastOrDefault(); // take newest
      if (apiDir != null)
      {
        var dllPath = new FileInfo(Path.Combine(apiDir.FullName, name));
        if (dllPath.Exists)
          hintPath = dllPath.FullName;
      }

      return hintPath;
    }

    private static void AddCustomReference(string name, XElement projectContentElement, XNamespace xmlns, string hintPath = null)
    {
      ourLogger.Verbose($"AddCustomReference {name}, {hintPath}");
      var itemGroup = projectContentElement.Elements(xmlns + "ItemGroup").FirstOrDefault();
      if (itemGroup == null)
      {
        ourLogger.Verbose("Skip AddCustomReference, ItemGroup is null.");
        return;
      }
      var reference = new XElement(xmlns + "Reference");
      reference.Add(new XAttribute("Include", Path.GetFileNameWithoutExtension(name)));
      if (!string.IsNullOrEmpty(hintPath))
        reference.Add(new XElement(xmlns + "HintPath", hintPath));
      itemGroup.Add(reference);
    }

    // Set appropriate version
    private static bool FixTargetFrameworkVersion(XElement projectElement, XNamespace xmlns)
    {
      return SetOrUpdateProperty(projectElement, xmlns, "TargetFrameworkVersion", s =>
        {
          if (UnityUtils.ScriptingRuntime > 0)
          {
            if (PluginSettings.OverrideTargetFrameworkVersion)
            {
              return "v" + PluginSettings.TargetFrameworkVersion;
            }
          }
          else
          {
            if (PluginSettings.OverrideTargetFrameworkVersionOldMono)
            {
              return "v" + PluginSettings.TargetFrameworkVersionOldMono;
            }
          }

          if (string.IsNullOrEmpty(s))
          {
            ourLogger.Verbose("TargetFrameworkVersion in csproj is null or empty.");
            return string.Empty;
          }

          var version = string.Empty;
          try
          {
            version = s.Substring(1);
            // for windows try to use installed dotnet framework
            // Unity 2018.1 doesn't require installed dotnet framework, it references everything from Unity installation
            if (PluginSettings.SystemInfoRiderPlugin.operatingSystemFamily == OperatingSystemFamilyRider.Windows && UnityUtils.UnityVersion < new Version(2018, 1))
            {
              var versions = PluginSettings.GetInstalledNetFrameworks();
              if (versions.Any())
              {
                var versionOrderedList = versions.OrderBy(v1 => new Version(v1));
                var foundVersion = UnityUtils.ScriptingRuntime > 0
                  ? versionOrderedList.Last()
                  : versionOrderedList.First();
                // Unity may require dotnet 4.7.1, which may not be present
                var fvIsParsed = VersionExtensions.TryParse(foundVersion, out var fv);
                var vIsParsed = VersionExtensions.TryParse(version, out var v);
                if (fvIsParsed && vIsParsed && (UnityUtils.ScriptingRuntime == 0 || UnityUtils.ScriptingRuntime > 0 && fv > v))
                  version = foundVersion;
                else if (foundVersion == version)
                  ourLogger.Verbose("Found TargetFrameworkVersion {0} equals the one set-by-Unity itself {1}",
                    foundVersion, version);
                else if (ourLogger.IsVersboseEnabled())
                {
                  var message = $"Rider may require \".NET Framework {version} Developer Pack\", which is not installed.";
                  Debug.Log(message);
                }
              }
            }
          }
          catch (Exception e)
          {
            ourLogger.Log(LoggingLevel.WARN, "Fail to FixTargetFrameworkVersion", e);
          }

          return "v" + version;
        }
      );
    }

    private static bool SetLangVersion(XElement projectElement, XNamespace xmlns)
    {
      // Set the C# language level, so Rider doesn't have to guess (although it does a good job)
      // VSTU sets this, and I think newer versions of Unity do too (should check which version)
      return SetOrUpdateProperty(projectElement, xmlns, "LangVersion", existing =>
      {
        if (PluginSettings.OverrideLangVersion)
        {
          return PluginSettings.LangVersion;
        }
        
        var expected = GetExpectedLanguageLevel();
        if (string.IsNullOrEmpty(existing))
          return expected;

        if (existing == "default")
          return expected;
        
        if (expected == "latest" || existing == "latest")
          return "latest";

        // Only use our version if it's not already set, or it's less than what we would set
        var currentIsParsed = VersionExtensions.TryParse(existing, out var currentLanguageLevel);
        var expectedIsParsed = VersionExtensions.TryParse(expected, out var expectedLanguageLevel);
        if (currentIsParsed && expectedIsParsed && currentLanguageLevel < expectedLanguageLevel)
        {
          return expected;
        }

        return existing;
      });
    }

    private static string GetExpectedLanguageLevel()
    {
      // https://bitbucket.org/alexzzzz/unity-c-5.0-and-6.0-integration/src
      if (Directory.Exists(Path.GetFullPath("CSharp70Support")))
        return "latest";
      if (Directory.Exists(Path.GetFullPath("CSharp60Support")))
        return "6";

      // Unity 5.5+ supports C# 6, but only when targeting .NET 4.6. The enum doesn't exist pre Unity 5.5
      if (ourApiCompatibilityLevel >= apiCompatibilityLevelNet46)
        return "6";

      return "4";
    }

    private static int GetApiCompatibilityLevel()
    {
      var apiCompatibilityLevel = 0;
      try
      {
        //PlayerSettings.GetApiCompatibilityLevel(EditorUserBuildSettings.selectedBuildTargetGroup)
        var method = typeof(PlayerSettings).GetMethod("GetApiCompatibilityLevel");
        var parameter = typeof(EditorUserBuildSettings).GetProperty("selectedBuildTargetGroup");
        var val = parameter.GetValue(null, null);
        apiCompatibilityLevel = (int) method.Invoke(null, new[] {val});
      }
      catch (Exception ex)
      {
        ourLogger.Verbose(
          "Exception on evaluating PlayerSettings.GetApiCompatibilityLevel(EditorUserBuildSettings.selectedBuildTargetGroup)" +
          ex);
      }

      try
      {
        var property = typeof(PlayerSettings).GetProperty("apiCompatibilityLevel");
        apiCompatibilityLevel = (int) property.GetValue(null, null);
      }
      catch (Exception)
      {
        ourLogger.Verbose("Exception on evaluating PlayerSettings.apiCompatibilityLevel");
      }

      return apiCompatibilityLevel;
    }

    private static bool SetProjectFlavour(XElement projectElement, XNamespace xmlns)
    {
      // This is the VSTU project flavour GUID, followed by the C# project type
      return SetOrUpdateProperty(projectElement, xmlns, "ProjectTypeGuids",
        "{E097FAD1-6243-4DAD-9C02-E9B9EFC3FFC1};{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}");
    }

    private static bool SetOrUpdateProperty(XElement root, XNamespace xmlns, string name, string content)
    {
      return SetOrUpdateProperty(root, xmlns, name, v => content);
    }

    private static bool SetOrUpdateProperty(XElement root, XNamespace xmlns, string name, Func<string, string> updater)
    {
      var element = root.Elements(xmlns + "PropertyGroup").Elements(xmlns + name).FirstOrDefault();
      if (element != null)
      {
        var result = updater(element.Value);
        if (result != element.Value)
        {
          ourLogger.Verbose("Overriding existing project property {0}. Old value: {1}, new value: {2}", name, element.Value, result);

          element.SetValue(result);
          return true;
        }

        ourLogger.Verbose("Property {0} already set. Old value: {1}, new value: {2}", name, element.Value, result);
      }
      else
      {
        AddProperty(root, xmlns, name, updater(string.Empty));
        return true;
      }

      return false;
    }

    // Adds a property to the first property group without a condition
    private static void AddProperty(XElement root, XNamespace xmlns, string name, string content)
    {
      ourLogger.Verbose("Adding project property {0}. Value: {1}", name, content);

      var propertyGroup = root.Elements(xmlns + "PropertyGroup")
        .FirstOrDefault(e => !e.Attributes(xmlns + "Condition").Any());
      if (propertyGroup == null)
      {
        propertyGroup = new XElement(xmlns + "PropertyGroup");
        root.AddFirst(propertyGroup);
      }

      propertyGroup.Add(new XElement(xmlns + name, content));
    }

    class Utf8StringWriter : StringWriter
    {
      public override Encoding Encoding => Encoding.UTF8;
    }
  }
}
