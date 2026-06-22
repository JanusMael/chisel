using FluentAssertions;
using Bennewitz.Ninja.Chisel.Resolution;

namespace Bennewitz.Ninja.Chisel.Tests;

public sealed class TypeResolverInternalsTests
{
    [Fact]
    public void PlainType_ProducesBareName()
    {
        TypeResolver.BuildMetadataNameCandidates("MyNS.Foo").Should().Contain("MyNS.Foo");
    }

    [Fact]
    public void GenericWithArity_AppendsBacktick()
    {
        TypeResolver.BuildMetadataNameCandidates("MyNS.Repository<T>").Should().Contain("MyNS.Repository`1");
    }

    [Fact]
    public void GenericTwoArgs_AppendsArity2()
    {
        TypeResolver.BuildMetadataNameCandidates("MyNS.Dict<K,V>").Should().Contain("MyNS.Dict`2");
    }

    [Fact]
    public void NestedTypeBySegment_TriesPlusSubstitution()
    {
        TypeResolver.BuildMetadataNameCandidates("MyNS.Outer.Inner").Should().Contain("MyNS.Outer+Inner");
    }

    [Fact]
    public void UnboundGenericOneSlot_InfersArity1()
    {
        // Regression: "Foo<>" must map to `Foo`1`, not the non-generic `Foo`.
        TypeResolver.BuildMetadataNameCandidates("MyNS.Repository<>").Should().Contain("MyNS.Repository`1");
    }

    [Fact]
    public void UnboundGenericTwoSlots_InfersArity2()
    {
        TypeResolver.BuildMetadataNameCandidates("MyNS.Dict<,>").Should().Contain("MyNS.Dict`2");
    }
}
