using Android.OS;
using Android.Views;

namespace PenguinMonitor.UI.Utils
{
    public class ViewInsetsListener : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        public WindowInsets OnApplyWindowInsets(View v, WindowInsets insets)
        {
            int topInset = 0;
            int bottomInset = 0;

            if (OperatingSystem.IsAndroidVersionAtLeast(21))
            {
                topInset = insets.SystemWindowInsetTop;
                bottomInset = insets.SystemWindowInsetBottom;

                if (OperatingSystem.IsAndroidVersionAtLeast(28) && insets.DisplayCutout != null)
                {
                    topInset = System.Math.Max(topInset, insets.DisplayCutout.SafeInsetTop);
                }
            }
            // Apply padding to avoid content being hidden behind system UI
            v.SetPadding(20, topInset + 20, 20, bottomInset + 20);

            return insets;
        }
    }
}