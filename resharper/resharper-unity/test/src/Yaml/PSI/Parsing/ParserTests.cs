﻿using JetBrains.ReSharper.Plugins.Unity.Yaml.Psi;
using JetBrains.ReSharper.TestFramework;
using NUnit.Framework;

namespace JetBrains.ReSharper.Plugins.Unity.Tests.Yaml.PSI.Parsing
{
  [TestFileExtension(".unity")]
  public class ParserTests : TestFramework.ParserTestBase<UnityYamlLanguage>
  {
    protected override string RelativeTestDataPath => @"Yaml\Psi\Parsing";

    [TestCase("Scene")]
    public void TestParser(string name) => DoOneTest(name);
  }
}