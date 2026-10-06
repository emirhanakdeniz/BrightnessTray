namespace BrightnessTray.Native;

using System.Runtime.InteropServices;

public static class AnimationInterop
{
    private const uint SPI_GETCLIENTAREAANIMATION = 0x1042;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(
        uint uiAction,
        uint uiParam,
        ref bool pvParam,
        uint fWinIni);

    public static bool IsAnimationEnabled()
    {
        try
        {
            bool isEnabled = true;
            if (SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, ref isEnabled, 0))
            {
                return isEnabled;
            }
        }
        catch
        {
            // Fail silently, default to true
        }

        return true;
    }
}
