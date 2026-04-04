namespace uk.osric.copilot.Tests.Unit {
    using NUnit.Framework;
    using uk.osric.copilot.Services;

    /// <summary>
    /// Tests for <see cref="EmailProcessorService.StripReplyPrefixes"/>.
    /// The method is <c>internal static</c>; visible via [assembly: InternalsVisibleTo(...)].
    /// </summary>
    [TestFixture]
    public class SubjectParserTests {
        [TestCase("TestProject", ExpectedResult = "TestProject")]
        [TestCase("Re: TestProject", ExpectedResult = "TestProject")]
        [TestCase("re: TestProject", ExpectedResult = "TestProject")]
        [TestCase("RE: TestProject", ExpectedResult = "TestProject")]
        [TestCase("Fwd: TestProject", ExpectedResult = "TestProject")]
        [TestCase("FW: TestProject", ExpectedResult = "TestProject")]
        [TestCase("Re: Fwd: TestProject", ExpectedResult = "TestProject")]
        [TestCase("Re: Re: FW: TestProject", ExpectedResult = "TestProject")]
        [TestCase("  Re: TestProject  ", ExpectedResult = "TestProject")]
        [TestCase("", ExpectedResult = "")]
        [TestCase("   ", ExpectedResult = "")]
        public string StripReplyPrefixes_ReturnsExpectedProjectName(string subject)
            => EmailProcessorService.StripReplyPrefixes(subject);

        [Test]
        public void StripReplyPrefixes_PreservesInternalSpaces() {
            var result = EmailProcessorService.StripReplyPrefixes("Re: My Cool Project");
            Assert.That(result, Is.EqualTo("My Cool Project"));
        }

        [Test]
        public void StripReplyPrefixes_DoesNotStripMatchesInMiddle() {
            var result = EmailProcessorService.StripReplyPrefixes("Review: something");
            Assert.That(result, Is.EqualTo("Review: something"));
        }
    }
}
