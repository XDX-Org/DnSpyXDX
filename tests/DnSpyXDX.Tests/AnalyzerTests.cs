using DnSpyXDX.Application;
using DnSpyXDX.Decompilation;
using Xunit;

namespace DnSpyXDX.Tests;

public sealed class AnalyzerTests
{
    [Fact]
    public async Task Reports_used_by_and_uses_only_for_kinds_that_support_them()
    {
        await using var backend = new DecompilerBackend();
        await backend.OpenAsync(typeof(AnalyzerTests).Assembly.Location);

        var method = await FindSymbol(backend, "Later", "Method");
        var field = await FindSymbol(backend, "SampleField", "Field");
        var type = await FindSymbol(backend, "SampleMembers", "Type");

        Assert.Equal([AnalyzerRelation.UsedBy, AnalyzerRelation.Uses], await backend.GetAnalyzerRelationsAsync(method));
        Assert.Equal([AnalyzerRelation.UsedBy], await backend.GetAnalyzerRelationsAsync(field));
        // SampleMembers is a sealed concrete class with a constructor: no Derived Types, but Instantiated By.
        Assert.Equal([AnalyzerRelation.UsedBy, AnalyzerRelation.InstantiatedBy, AnalyzerRelation.ExposedBy], await backend.GetAnalyzerRelationsAsync(type));
    }

    [Fact]
    public async Task Used_by_lists_methods_that_call_a_method()
    {
        await using var backend = new DecompilerBackend();
        await backend.OpenAsync(typeof(AnalyzerTests).Assembly.Location);
        var later = await FindSymbol(backend, "Later", "Method");

        var callers = await backend.AnalyzeAsync(later, AnalyzerRelation.UsedBy);

        var caller = Assert.Single(callers, result => result.Name == "CallsLater");
        Assert.Equal(TreeNodeKind.Method, caller.Kind);
        Assert.NotNull(caller.ILOffset);
    }

    [Fact]
    public async Task Used_by_lists_methods_that_read_or_write_a_field()
    {
        await using var backend = new DecompilerBackend();
        await backend.OpenAsync(typeof(AnalyzerTests).Assembly.Location);
        var field = await FindSymbol(backend, "SampleField", "Field");

        var callers = await backend.AnalyzeAsync(field, AnalyzerRelation.UsedBy);
        var names = callers.Select(result => result.Name).ToHashSet();

        Assert.Contains("Combined", names); // reads and writes SampleField
        Assert.Contains("Doubled", names);  // extension method reads member.SampleField
    }

    [Fact]
    public async Task Uses_lists_the_members_a_method_references()
    {
        await using var backend = new DecompilerBackend();
        await backend.OpenAsync(typeof(AnalyzerTests).Assembly.Location);
        var use = await FindSymbol(backend, "Use", "Method");

        var uses = await backend.AnalyzeAsync(use, AnalyzerRelation.Uses);
        var names = uses.Select(result => result.Name).ToHashSet();

        Assert.Contains("SampleMethod", names); // member.SampleMethod()
        Assert.Contains("Doubled", names);      // member.Doubled() extension call
    }

    [Fact]
    public async Task Type_offers_derived_and_instantiated_relations()
    {
        await using var backend = new DecompilerBackend();
        await backend.OpenAsync(typeof(AnalyzerTests).Assembly.Location);

        var baseType = await FindSymbol(backend, "AnalyzerBase", "Type");
        var derived = await FindSymbol(backend, "AnalyzerDerived", "Type");

        // AnalyzerBase is an abstract class with a base type, so it can be derived from but not instantiated.
        Assert.Equal([AnalyzerRelation.UsedBy, AnalyzerRelation.DerivedTypes, AnalyzerRelation.ExposedBy], await backend.GetAnalyzerRelationsAsync(baseType));
        // AnalyzerDerived is a concrete sealed class with a constructor, so it can be instantiated but not derived from.
        Assert.Equal([AnalyzerRelation.UsedBy, AnalyzerRelation.InstantiatedBy, AnalyzerRelation.ExposedBy], await backend.GetAnalyzerRelationsAsync(derived));
    }

    [Fact]
    public async Task Derived_types_lists_subclasses()
    {
        await using var backend = new DecompilerBackend();
        await backend.OpenAsync(typeof(AnalyzerTests).Assembly.Location);
        var baseType = await FindSymbol(backend, "AnalyzerBase", "Type");

        var derived = await backend.AnalyzeAsync(baseType, AnalyzerRelation.DerivedTypes);

        var result = Assert.Single(derived, item => item.Name == "AnalyzerDerived");
        Assert.Equal(TreeNodeKind.Type, result.Kind);
    }

    [Fact]
    public async Task Instantiated_by_lists_methods_that_construct_the_type()
    {
        await using var backend = new DecompilerBackend();
        await backend.OpenAsync(typeof(AnalyzerTests).Assembly.Location);
        var derived = await FindSymbol(backend, "AnalyzerDerived", "Type");

        var callers = await backend.AnalyzeAsync(derived, AnalyzerRelation.InstantiatedBy);

        Assert.Contains(callers, result => result.Name == "Make"); // AnalyzerFactory.Make news up AnalyzerDerived
    }

    [Fact]
    public async Task Method_offers_override_relations_matching_its_role()
    {
        await using var backend = new DecompilerBackend();
        await backend.OpenAsync(typeof(AnalyzerTests).Assembly.Location);

        var baseRun = await FindMethod(backend, "AnalyzerBase.Run");
        var derivedRun = await FindMethod(backend, "AnalyzerDerived.Run");
        var interfaceServe = await FindMethod(backend, "IAnalyzerService.Serve");

        // A fresh virtual can be overridden but overrides nothing; a sealed override overrides but is final.
        Assert.Equal([AnalyzerRelation.UsedBy, AnalyzerRelation.Uses, AnalyzerRelation.OverriddenBy], await backend.GetAnalyzerRelationsAsync(baseRun));
        Assert.Equal([AnalyzerRelation.UsedBy, AnalyzerRelation.Uses, AnalyzerRelation.Overrides], await backend.GetAnalyzerRelationsAsync(derivedRun));
        Assert.Equal([AnalyzerRelation.UsedBy, AnalyzerRelation.Uses, AnalyzerRelation.ImplementedBy], await backend.GetAnalyzerRelationsAsync(interfaceServe));
    }

    [Fact]
    public async Task Overrides_and_overridden_by_pair_a_base_method_with_its_override()
    {
        await using var backend = new DecompilerBackend();
        await backend.OpenAsync(typeof(AnalyzerTests).Assembly.Location);
        var baseRun = await FindMethod(backend, "AnalyzerBase.Run");
        var derivedRun = await FindMethod(backend, "AnalyzerDerived.Run");

        var overrides = await backend.AnalyzeAsync(derivedRun, AnalyzerRelation.Overrides);
        var overriddenBy = await backend.AnalyzeAsync(baseRun, AnalyzerRelation.OverriddenBy);

        Assert.Contains(overrides, result => result.QualifiedName!.EndsWith("AnalyzerBase.Run", StringComparison.Ordinal));
        Assert.Contains(overriddenBy, result => result.QualifiedName!.EndsWith("AnalyzerDerived.Run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Implemented_by_lists_interface_implementers()
    {
        await using var backend = new DecompilerBackend();
        await backend.OpenAsync(typeof(AnalyzerTests).Assembly.Location);
        var serve = await FindMethod(backend, "IAnalyzerService.Serve");

        var implementers = await backend.AnalyzeAsync(serve, AnalyzerRelation.ImplementedBy);

        Assert.Contains(implementers, result => result.QualifiedName!.EndsWith("AnalyzerService.Serve", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Exposed_by_lists_members_whose_signature_uses_the_type()
    {
        await using var backend = new DecompilerBackend();
        await backend.OpenAsync(typeof(AnalyzerTests).Assembly.Location);
        var baseType = await FindSymbol(backend, "AnalyzerBase", "Type");

        var exposers = await backend.AnalyzeAsync(baseType, AnalyzerRelation.ExposedBy);

        // AnalyzerFactory.Make returns AnalyzerBase, exposing it in its signature.
        Assert.Contains(exposers, result => result.Name == "Make");
    }

    [Fact]
    public async Task Property_and_event_offer_relations()
    {
        await using var backend = new DecompilerBackend();
        await backend.OpenAsync(typeof(AnalyzerTests).Assembly.Location);

        var baseLevel = await FindMember(backend, "Property", "AnalyzerPropertyHost.Level");
        var overrideLevel = await FindMember(backend, "Property", "AnalyzerPropertyOverride.Level");
        var interfaceCount = await FindMember(backend, "Property", "IAnalyzerProperty.Count");
        var pinged = await FindMember(backend, "Event", "AnalyzerPropertyHost.Pinged");

        Assert.Equal([AnalyzerRelation.UsedBy, AnalyzerRelation.OverriddenBy], await backend.GetAnalyzerRelationsAsync(baseLevel));
        Assert.Equal([AnalyzerRelation.UsedBy, AnalyzerRelation.Overrides], await backend.GetAnalyzerRelationsAsync(overrideLevel));
        Assert.Equal([AnalyzerRelation.UsedBy, AnalyzerRelation.ImplementedBy], await backend.GetAnalyzerRelationsAsync(interfaceCount));
        // A field-like event has a backing field, so Event Fired By is offered alongside Used By.
        Assert.Equal([AnalyzerRelation.UsedBy, AnalyzerRelation.EventFiredBy], await backend.GetAnalyzerRelationsAsync(pinged));
    }

    [Fact]
    public async Task Event_fired_by_lists_methods_that_raise_the_event()
    {
        await using var backend = new DecompilerBackend();
        await backend.OpenAsync(typeof(AnalyzerTests).Assembly.Location);
        var pinged = await FindMember(backend, "Event", "AnalyzerPropertyHost.Pinged");

        var raisers = (await backend.AnalyzeAsync(pinged, AnalyzerRelation.EventFiredBy)).Select(result => result.Name).ToHashSet();

        Assert.Contains("Raise", raisers);          // loads Pinged and calls Invoke
        Assert.DoesNotContain("Subscribe", raisers); // only subscribes, does not fire
    }

    [Fact]
    public async Task Used_by_follows_property_and_event_accessors()
    {
        await using var backend = new DecompilerBackend();
        await backend.OpenAsync(typeof(AnalyzerTests).Assembly.Location);
        var level = await FindMember(backend, "Property", "AnalyzerPropertyHost.Level");
        var pinged = await FindMember(backend, "Event", "AnalyzerPropertyHost.Pinged");

        var levelUsers = (await backend.AnalyzeAsync(level, AnalyzerRelation.UsedBy)).Select(result => result.Name).ToHashSet();
        var eventUsers = (await backend.AnalyzeAsync(pinged, AnalyzerRelation.UsedBy)).Select(result => result.Name).ToHashSet();

        Assert.Contains("ReadLevel", levelUsers); // calls get_Level
        Assert.Contains("SetLevel", levelUsers);  // calls set_Level
        Assert.Contains("Subscribe", eventUsers); // calls add_Pinged
    }

    [Fact]
    public async Task Property_override_and_interface_relations_resolve()
    {
        await using var backend = new DecompilerBackend();
        await backend.OpenAsync(typeof(AnalyzerTests).Assembly.Location);
        var baseLevel = await FindMember(backend, "Property", "AnalyzerPropertyHost.Level");
        var overrideLevel = await FindMember(backend, "Property", "AnalyzerPropertyOverride.Level");
        var interfaceCount = await FindMember(backend, "Property", "IAnalyzerProperty.Count");

        var overriddenBy = await backend.AnalyzeAsync(baseLevel, AnalyzerRelation.OverriddenBy);
        var overrides = await backend.AnalyzeAsync(overrideLevel, AnalyzerRelation.Overrides);
        var implementedBy = await backend.AnalyzeAsync(interfaceCount, AnalyzerRelation.ImplementedBy);

        Assert.Contains(overriddenBy, result => result.QualifiedName!.EndsWith("AnalyzerPropertyOverride.Level", StringComparison.Ordinal));
        Assert.Contains(overrides, result => result.QualifiedName!.EndsWith("AnalyzerPropertyHost.Level", StringComparison.Ordinal));
        Assert.Contains(implementedBy, result => result.QualifiedName!.EndsWith("AnalyzerPropertyImpl.Count", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Used_by_is_empty_for_a_method_nothing_calls()
    {
        await using var backend = new DecompilerBackend();
        await backend.OpenAsync(typeof(AnalyzerTests).Assembly.Location);
        var ping = await FindSymbol(backend, "Ping", "Method");

        Assert.Empty(await backend.AnalyzeAsync(ping, AnalyzerRelation.UsedBy));
    }

    private static async Task<SymbolId> FindSymbol(IDecompilerBackend backend, string name, string kind)
    {
        var results = await backend.SearchAsync(name);
        return results.First(result => result.Name == name && result.Kind == kind).Symbol;
    }

    private static Task<SymbolId> FindMethod(IDecompilerBackend backend, string qualifiedSuffix) => FindMember(backend, "Method", qualifiedSuffix);

    private static async Task<SymbolId> FindMember(IDecompilerBackend backend, string kind, string qualifiedSuffix)
    {
        var results = await backend.SearchAsync(qualifiedSuffix[(qualifiedSuffix.LastIndexOf('.') + 1)..]);
        return results.First(result => result.Kind == kind && result.QualifiedName is { } qualified && qualified.EndsWith(qualifiedSuffix, StringComparison.Ordinal)).Symbol;
    }
}
