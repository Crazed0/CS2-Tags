using CounterStrikeSharp.API.Modules.Utils;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace CS2_Tags;

public struct RgbColor
{
    public int R;
    public int G;
    public int B;

    public RgbColor(int r, int g, int b)
    {
        R = r;
        G = g;
        B = b;
    }
}

public static class CS2_TagsHelper
{
    private static readonly Dictionary<string, RgbColor> ChatColorsMap = new()
    {
        { "{Default}", new RgbColor(255, 255, 255) },
        { "{DarkRed}", new RgbColor(139, 0, 0) },
        { "{Green}", new RgbColor(0, 128, 0) },
        { "{LightYellow}", new RgbColor(255, 255, 224) },
        { "{LightBlue}", new RgbColor(173, 216, 230) },
        { "{Olive}", new RgbColor(128, 128, 0) },
        { "{Lime}", new RgbColor(0, 255, 0) },
        { "{Red}", new RgbColor(255, 0, 0) },
        { "{Purple}", new RgbColor(128, 0, 128) },
        { "{Grey}", new RgbColor(128, 128, 128) },
        { "{Yellow}", new RgbColor(255, 255, 0) },
        { "{Gold}", new RgbColor(255, 215, 0) },
        { "{Silver}", new RgbColor(192, 192, 192) },
        { "{Blue}", new RgbColor(0, 0, 255) },
        { "{DarkBlue}", new RgbColor(0, 0, 139) },
        { "{BlueGrey}", new RgbColor(102, 153, 204) },
        { "{Magenta}", new RgbColor(255, 0, 255) },
        { "{LightRed}", new RgbColor(255, 102, 102) }
    };

    private static RgbColor ParseHex(string hex)
    {
        hex = hex.StartsWith("#") ? hex.Substring(1) : hex;
        
        if (hex.Length == 3)
        {
            hex = "" + hex[0] + hex[0] + hex[1] + hex[1] + hex[2] + hex[2];
        }

        if (hex.Length == 6)
        {
            int r = int.Parse(hex.Substring(0, 2), NumberStyles.HexNumber);
            int g = int.Parse(hex.Substring(2, 2), NumberStyles.HexNumber);
            int b = int.Parse(hex.Substring(4, 2), NumberStyles.HexNumber);
            return new RgbColor(r, g, b);
        }

        return new RgbColor(255, 255, 255);
    }

    /// <summary>
    /// Calcula a cor do ChatColor mais próxima de uma cor HEX via distância Euclidiana RGB.
    /// </summary>
    public static string GetClosestChatColor(string hexCode)
    {
        if (string.IsNullOrEmpty(hexCode)) return "{Default}";

        try
        {
            RgbColor targetColor = ParseHex(hexCode);
            
            string closestMatch = "{Default}";
            double minDistance = double.MaxValue;

            foreach (var kvp in ChatColorsMap)
            {
                RgbColor mapColor = kvp.Value;
                
                // Distância Euclidiana
                long rDiff = targetColor.R - mapColor.R;
                long gDiff = targetColor.G - mapColor.G;
                long bDiff = targetColor.B - mapColor.B;
                
                double distance = Math.Sqrt((rDiff * rDiff) + (gDiff * gDiff) + (bDiff * bDiff));

                if (distance < minDistance)
                {
                    minDistance = distance;
                    closestMatch = kvp.Key;
                }
            }

            return closestMatch;
        }
        catch
        {
            return "{Default}";
        }
    }
}
