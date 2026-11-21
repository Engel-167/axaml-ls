using AxamlLs.Analysis.Refactorings;

namespace AxamlLs.Tests;

[Collection("sample")]
public class ConvertToCompiledBindingTests(SampleProjectFixture sample)
{
    private readonly RefactoringTestSupport _r = new(sample);
    private readonly ConvertToCompiledBinding _convert = new();

    [Fact]
    public void A_reflection_binding_that_checks_becomes_a_binding()
    {
        var after = _r.Apply(_convert, RefactoringTestSupport.Header + "\n    <TextBlock Text=\"{Reflection|Binding Title}\"/>\n</UserControl>");
        Assert.Matches("Text=\"\\{(Compiled)?Binding Title\\}\"", after);
    }

    [Fact]
    public void Not_offered_when_the_path_does_not_resolve_or_there_is_no_data_type()
    {
        Assert.Null(_r.Title(_convert, RefactoringTestSupport.Header + "\n    <TextBlock Text=\"{Reflection|Binding NoSuchMember}\"/>\n</UserControl>"));
        Assert.Null(_r.Title(_convert, "<UserControl xmlns=\"https://github.com/avaloniaui\">\n    <TextBlock Text=\"{Reflection|Binding Title}\"/>\n</UserControl>"));
    }
}
