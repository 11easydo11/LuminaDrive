using System;
using System.IO;

namespace UsbRecoverySuite.UI;

internal static class PrivacyConsentStore
{
    private static readonly string ConsentFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UsbRecoverySuite",
        "privacy-consent.txt");

    public static bool HasAccepted()
    {
        try
        {
            return File.ReadAllText(ConsentFilePath).Trim() == "accepted-v1";
        }
        catch
        {
            return false;
        }
    }

    public static void SaveAcceptance()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConsentFilePath)!);
        File.WriteAllText(ConsentFilePath, "accepted-v1");
    }
}
