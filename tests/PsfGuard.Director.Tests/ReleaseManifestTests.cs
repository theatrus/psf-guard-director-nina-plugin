using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
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
            ["URL"] = "https://github.com/theatrus/psf-guard-director-nina-plugin/releases/download/0.1.0.0-preview.1/PSFGuardDirector-0.1.0.0.zip",
            ["Type"] = "ARCHIVE",
            ["ChecksumType"] = "SHA256",
            ["Checksum"] = new string('A', 64)
        };
        var schema = await NJsonSchema.JsonSchema.FromJsonAsync(NINA.Plugin.ManifestDefinition.PluginManifest.Schema);
        Assert.Empty(schema.Validate(template.ToJsonString()));
    }

    [Fact]
    public void RegistryTemplateMatchesCompiledPluginAndCannotClaimAcquisition()
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
        Assert.Contains("Acquisition is not yet available", metadata["ShortDescription"]);
        Assert.Null(template["Channel"]);
        Assert.Contains(template["Tags"]!.AsArray(), tag => tag!.GetValue<string>() == "experimental");
        Assert.Null(template["Installer"]);
    }
}
