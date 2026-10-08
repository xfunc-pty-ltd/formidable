using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Formidable.AspNetCore.Tests;

public class AotCompatibilityTests
{
    private static readonly Assembly Package = typeof(ValidateAttribute).Assembly;

    // The declaration turns on the AOT analyser for the package's own build, and the build records
    // it in the assembly's metadata. Mutation: drop IsAotCompatible from
    // Formidable.AspNetCore.csproj, and the metadata entry this looks for is not emitted.
    [Fact]
    public void The_package_declares_itself_AOT_compatible()
    {
        var declared = Package.GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => attribute.Key == "IsAotCompatible");

        Assert.NotNull(declared);
        Assert.Equal("True", declared.Value);
    }

    // A Native AOT app validates with Validate<TModel>(), so the MVC attribute is the one member
    // that may ask a consumer for dynamic code. Mutation: put RequiresDynamicCode on a
    // Validate<TModel>() overload, and the list gains it. Taking it off ValidateAttribute stops
    // the build first, on four IL3050 errors, while the package declares itself AOT-compatible.
    [Fact]
    public void Only_the_MVC_attribute_requires_dynamic_code()
    {
        const BindingFlags Declared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        var marked = Package.GetExportedTypes()
            .SelectMany(type => type.GetMembers(Declared).Prepend(type))
            .Where(member => member.IsDefined(typeof(RequiresDynamicCodeAttribute), inherit: false))
            .Select(member => member is Type type ? type.FullName : $"{member.DeclaringType!.FullName}.{member.Name}")
            .ToList();

        Assert.Equal(["Formidable.AspNetCore.ValidateAttribute"], marked);
    }
}
