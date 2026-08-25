namespace EasyImageSharp;

internal static class Guard
{
    public static void MustBePositive(int value, string parameterName)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Value must be greater than zero.");
        }
    }
}
