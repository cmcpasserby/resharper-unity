using JetBrains.Application.Settings;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.FeaturesTestFramework.Intentions;
using JetBrains.ReSharper.Plugins.Unity.CSharp.Daemon.Stages.PerformanceCriticalCodeAnalysis.Highlightings;
using JetBrains.ReSharper.Plugins.Unity.CSharp.Feature.Services.QuickFixes.MoveQuickFixes;
using JetBrains.ReSharper.Psi;
using NUnit.Framework;

namespace JetBrains.ReSharper.Plugins.Unity.Tests.CSharp.Intentions.QuickFixes
{
    [TestUnity]
    public class MoveNullComparisonQuickFixAvailabilityTests : QuickFixAvailabilityTestBase
    {
        protected override string RelativeTestDataPath=> @"CSharp\Intentions\QuickFixes\MoveNullComparison\Availability";

        [Test][Ignore("AvailabilityTestBase does not support global analysis")]  public void EveryThingAvailable() { DoNamedTest(); }
        [Test][Ignore("AvailabilityTestBase does not support global analysis")]  public void NotAvailableDueToLocalDependencies1() { DoNamedTest(); }
        [Test][Ignore("AvailabilityTestBase does not support global analysis")]  public void NotAvailableDueToLocalDependencies2() { DoNamedTest(); }
        [Test][Ignore("AvailabilityTestBase does not support global analysis")]  public void NotAvailableDueToMissedTypeArgument() {DoNamedTest(); }

        protected override bool HighlightingPredicate(IHighlighting highlighting, IPsiSourceFile psiSourceFile,
            IContextBoundSettingsStore boundSettingsStore)
        {
            IHighlightingTestBehaviour highlightingTestBehaviour = highlighting as IHighlightingTestBehaviour;
            return (highlightingTestBehaviour == null || !highlightingTestBehaviour.IsSuppressed) && highlighting is PerformanceNullComparisonHighlighting;
        }
    }

    
    [TestUnity]
    public class MoveNullComparisonQuickFixTests : CSharpQuickFixAfterSwaTestBase<MoveNullComparisonQuickFix>
    {
        protected override string RelativeTestDataPath=> @"CSharp\Intentions\QuickFixes\MoveNullComparison";

        [Test] public void MoveToStart() { DoNamedTest(); }
        [Test] public void MoveToAwake() { DoNamedTest(); }
        [Test] public void MoveOutsideTheLoop() { DoNamedTest(); }
        [Test] public void CorrectNameGeneration() {DoNamedTest(); }
        [Test] public void CorrectNameGeneration1() {DoNamedTest(); }
        [Test] public void CorrectNameGeneration2() {DoNamedTest(); }
    }
}