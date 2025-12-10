using Android.Views;
using PenguinMonitor.UI.Factories;

namespace PenguinMonitor.UI.Gestures
{
    public class SwipeGestureDetector : GestureDetector.SimpleOnGestureListener
    {
        private readonly MainActivity _activity;
        private readonly int _swipeThreshold;
        private const int SWIPE_VELOCITY_THRESHOLD = 100;

        public SwipeGestureDetector(MainActivity activity)
        {
            _activity = activity;
            // Set threshold to 1/4 of screen width for more intentional swipes
            var screenWidth = activity.Resources?.DisplayMetrics?.WidthPixels ?? 1080;
            _swipeThreshold = screenWidth / 4;
        }

        public override bool OnFling(MotionEvent? e1, MotionEvent e2, float velocityX, float velocityY)
        {
            if (e1 == null || e2 == null) return false;

            float diffX = e2.GetX() - e1.GetX();
            float diffY = e2.GetY() - e1.GetY();

            // Check if it's a horizontal swipe (not vertical)
            if (System.Math.Abs(diffX) > System.Math.Abs(diffY))
            {
                // Check if swipe distance and velocity are sufficient
                if (System.Math.Abs(diffX) > _swipeThreshold && System.Math.Abs(velocityX) > SWIPE_VELOCITY_THRESHOLD)
                {
                    // ONLY allow swipes on single box page in content area
                    bool isOnSingleBoxPage = _activity.selectedPage == UIFactory.selectedPage.BoxDataSingle;
                    bool isInContentArea = _activity.IsTouchInContentArea(e1.GetY());

                    if (isOnSingleBoxPage && isInContentArea)
                    {
                        // Historical data navigation: LEFT = older (from left), RIGHT = newer (from right)
                        // Reading left-to-right timeline: older is on the left
                        if (diffX > 0)
                        {
                            // Swipe right → older data (slides from left)
                            _activity.OnSwipeNext();
                        }
                        else
                        {
                            // Swipe left → newer data (slides from right)
                            _activity.OnSwipePrevious();
                        }
                        return true;
                    }
                }
            }
            return false;
        }
    }
}