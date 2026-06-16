using System.Reflection;
using Xunit;

namespace PAXCookbook.Tests;

public class ProgramSmokeTests
{
    [Fact]
    public void App_assembly_contains_Program()
    {
        var asm = Assembly.Load("PAXCookbook");
        var t = asm.GetType("PAXCookbook.Program");
        Assert.NotNull(t);
    }
}
