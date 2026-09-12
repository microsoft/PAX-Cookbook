namespace SectionB.NegativeFixture;

public static class NegativeFixture
{
    public const string HarmlessLiteral = "Process.Start";

    public static int HarmlessMethod() => HarmlessLiteral.Length;
}