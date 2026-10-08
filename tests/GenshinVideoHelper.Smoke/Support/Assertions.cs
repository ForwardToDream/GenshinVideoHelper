namespace GenshinVideoHelper.Smoke;

internal static class Assertions
{
    public static void Check(bool passed, string label)
    {
        if (!passed) throw new InvalidOperationException("FAIL: " + label);
    }
}
