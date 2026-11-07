using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows.Media.Imaging;
using PsfGuard.Director.Plugin;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class ReleaseManifestTests
{
    [Fact]
    public async Task CompletedTemplateValidatesAgainstPinnedNinaSchema()
    {
        var template = JsonNode.Parse(File.ReadAllText(Path.Combine(ProcessTests.BundleDirectory, "packaging", "manifest.template.json")))!;
        template["Installer"] = new JsonObject
        {
            ["URL"] = "https://github.com/theatrus/psf-guard-director-nina-plugin/releases/download/0.1.0.0-beta.1/PSFGuardDirector-0.1.0.0.zip",
            ["Type"] = "ARCHIVE",
            ["ChecksumType"] = "SHA256",
            ["Checksum"] = new string('A', 64)
        };
        var schema = await NJsonSchema.JsonSchema.FromJsonAsync(NINA.Plugin.ManifestDefinition.PluginManifest.Schema);
        Assert.Empty(schema.Validate(template.ToJsonString()));
    }

    [Fact]
    public void RegistryTemplateMatchesCompiledPluginWithBetaLabelAndIcon()
    {
        var template = JsonNode.Parse(File.ReadAllText(Path.Combine(ProcessTests.BundleDirectory, "packaging", "manifest.template.json")))!;
        var assembly = typeof(DirectorPlugin).Assembly;
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(value => value.Key, value => value.Value);
        Assert.Equal(assembly.GetName().Name, template["Name"]!.GetValue<string>());
        Assert.Equal(assembly.GetCustomAttribute<GuidAttribute>()!.Value, template["Identifier"]!.GetValue<string>());
        Assert.Equal(assembly.GetCustomAttribute<AssemblyCompanyAttribute>()!.Company, template["Author"]!.GetValue<string>());
        var parts = new[] { "Major", "Minor", "Patch", "Build" };
        var version = new Version(string.Join('.', parts.Select(part => template["Version"]![part]!.GetValue<int>())));
        Assert.Equal(assembly.GetName().Version, version);
        Assert.Equal(metadata["MinimumApplicationVersion"], string.Join('.', parts.Select(part => template["MinimumApplicationVersion"]![part]!.GetValue<int>())));
        foreach (var key in new[] { "Homepage", "Repository", "License", "LicenseURL" })
            Assert.Equal(metadata[key], template[key]!.GetValue<string>());
        Assert.Equal(metadata["ShortDescription"], template["Descriptions"]!["ShortDescription"]!.GetValue<string>());
        Assert.Equal(metadata["ShortDescription"], assembly.GetCustomAttribute<AssemblyDescriptionAttribute>()!.Description);
        Assert.StartsWith("Beta ", metadata["ShortDescription"]);
        Assert.Contains("local target scheduling", metadata["ShortDescription"]);
        var description = template["Descriptions"]!["LongDescription"]!.GetValue<string>();
        Assert.Equal(metadata["LongDescription"], description);
        Assert.Contains("native NINA centering", description);
        Assert.Contains("Recovers interrupted assignments using current image counts", description);
        Assert.Contains("server with automatic session recovery support", description);
        Assert.Contains("Real-sky full-night acceptance remains open", description);
        Assert.DoesNotContain("Settled-night restart is opt-in", description);
        Assert.Contains("Do not use for unattended imaging", description);
        Assert.Null(template["Channel"]);
        Assert.Contains(template["Tags"]!.AsArray(), tag => tag!.GetValue<string>() == "beta");
        Assert.Contains(template["Tags"]!.AsArray(), tag => tag!.GetValue<string>() == "experimental");
        const string iconUrl = "https://raw.githubusercontent.com/theatrus/psf-guard-director-nina-plugin/main/assets/psf-guard-director.png";
        Assert.Equal(iconUrl, metadata["FeaturedImageURL"]);
        Assert.Equal(iconUrl, template["Descriptions"]!["FeaturedImageURL"]!.GetValue<string>());
        using var iconFile = File.OpenRead(Path.Combine(ProcessTests.BundleDirectory, "assets", "psf-guard-director.png"));
        var icon = BitmapDecoder.Create(iconFile, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        Assert.Equal(256, icon.PixelWidth);
        Assert.Equal(256, icon.PixelHeight);
        Assert.Null(template["Installer"]);
    }
}
