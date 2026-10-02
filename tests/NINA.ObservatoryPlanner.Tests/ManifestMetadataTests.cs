using FluentAssertions;
using NUnit.Framework;
using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace NINA.ObservatoryPlanner.Tests {

    /// <summary>
    /// NINA's CreateManifest.ps1 builds the plugin manifest from these assembly attributes; the manifest repository
    /// rejects a manifest without the required fields, and the Identifier must never change between releases.
    /// </summary>
    [TestFixture]
    public class ManifestMetadataTests {
        private static readonly Assembly Plugin = typeof(ObservatoryPlannerPlugin).Assembly;

        private static string Meta(string key) => Plugin.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value;

        [Test]
        public void Identifier_name_author_and_description_are_set() {
            Plugin.GetCustomAttribute<GuidAttribute>().Value.Should().Be("6f3a2c1e-8b7d-4e2a-9c51-0d4b7e8a91f2", "the plugin's identifier must never change");
            Plugin.GetCustomAttribute<AssemblyTitleAttribute>().Title.Should().Be("Observatory Planner");
            Plugin.GetCustomAttribute<AssemblyCompanyAttribute>().Company.Should().NotBeNullOrWhiteSpace();
            Plugin.GetCustomAttribute<AssemblyDescriptionAttribute>().Description.Should().NotBeNullOrWhiteSpace();
            Version.Parse(Plugin.GetCustomAttribute<AssemblyFileVersionAttribute>().Version).Should().BeGreaterThanOrEqualTo(new Version(1, 0, 0, 0));
        }

        [TestCase("MinimumApplicationVersion")]
        [TestCase("License")]
        [TestCase("LicenseURL")]
        [TestCase("Repository")]
        [TestCase("Homepage")]
        [TestCase("ChangelogURL")]
        [TestCase("LongDescription")]
        [TestCase("FeaturedImageURL")]
        [TestCase("ScreenshotURL")]
        [TestCase("AltScreenshotURL")]
        public void Manifest_metadata_is_present(string key) {
            Meta(key).Should().NotBeNullOrWhiteSpace();
            if (key.EndsWith("URL") || key is "Repository" or "Homepage") {
                Uri.TryCreate(Meta(key), UriKind.Absolute, out var uri).Should().BeTrue();
                uri.Scheme.Should().Be("https");
            }
        }

        [Test]
        public void Minimum_NINA_version_matches_the_plugin_package_it_is_built_against() {
            Meta("MinimumApplicationVersion").Should().Be("3.2.0.9001");
        }
    }
}
