using Android.Views;

namespace PenguinMonitor.UI.Gestures
{
    public class SwipeGestureDetector : GestureDetector.SimpleOnGestureListener
    {
        private readonly MainActivity _activity;
        private const int SWIPE_THRESHOLD = 100;
        private const int SWIPE_VELOCITY_THRESHOLD = 100;

        public SwipeGestureDetector(MainActivity activity)
        {
            _activity = activity;
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
                if (System.Math.Abs(diffX) > SWIPE_THRESHOLD && System.Math.Abs(velocityX) > SWIPE_VELOCITY_THRESHOLD)
                {
                    if (diffX > 0)
                    {
                        // Swipe right ? Previous box
                        _activity.OnSwipePrevious();
                    }
                    else
                    {
                        // Swipe left ? Next box
                        _activity.OnSwipeNext();
                    }
                    return true;
                }
            }
            return false;
        }
    }
}