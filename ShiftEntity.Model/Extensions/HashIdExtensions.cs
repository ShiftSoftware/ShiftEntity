namespace System;

public static class HashIdExtensions
{
    public static long ToLong(this string ID)
    {
        return long.Parse(ID);
    }
}
