using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;

namespace BluePenguinMonitoring.UI.Factories
{
    public class UIFactory
    {
        private readonly Context _context;
        
        // Color constants
        public static readonly Color PRIMARY_COLOR = Color.ParseColor("#2196F3");
        public static readonly Color PRIMARY_DARK = Color.ParseColor("#1976D2");
        public static readonly Color SUCCESS_COLOR = Color.ParseColor("#4CAF50");
        public static readonly Color WARNING_COLOR = Color.ParseColor("#FF9800");
        public static readonly Color DANGER_COLOR = Color.ParseColor("#F44336");
        public static readonly Color TEXT_FIELD_BACKGROUND_COLOR = Color.ParseColor("#F0F0F0");
        public static readonly Color BACKGROUND_COLOR = Color.LightGray;
        public static readonly Color CARD_COLOR = Color.White;
        public static readonly Color TEXT_PRIMARY = Color.ParseColor("#212121");
        public static readonly Color TEXT_SECONDARY = Color.ParseColor("#757575");
        
        // Add alternating row colors for bird scans
        public static readonly Color SCAN_ROW_EVEN = Color.ParseColor("#FAFAFA");
        public static readonly Color SCAN_ROW_ODD = Color.ParseColor("#F5F5F5");

        // Add sex-based background colors
        public static readonly Color FEMALE_BACKGROUND = Color.ParseColor("#FFE4E1"); // Light pink
        public static readonly Color MALE_BACKGROUND = Color.ParseColor("#E6F3FF"); // Light blue
        public static readonly Color CHICK_BACKGROUND = Color.ParseColor("#FFFFE6");   // Light yellow

        public UIFactory(Context context)
        {
            _context = context;
        }

        public enum selectedPage
        {
            Settings,
            BoxDataSingle,
            BoxDataMany
        }

        public LinearLayout CreateCard(Orientation orientation=Orientation.Vertical)
        {
            var card = new LinearLayout(_context)
            {
                Orientation = orientation
            };

            card.SetPadding(20, 16, 20, 16);
            card.Background = CreateCardBackground();

            var cardParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            cardParams.SetMargins(0, 0, 0, 16);
            card.LayoutParameters = cardParams;

            return card;
        }
        public GradientDrawable CreateCardBackground()
        {
            var drawable = new GradientDrawable();
            drawable.SetColor(CARD_COLOR);
            drawable.SetCornerRadius(12 * _context.Resources?.DisplayMetrics?.Density ?? 12);
            drawable.SetStroke(1, Color.ParseColor("#E0E0E0"));
            return drawable;
        }
        public GradientDrawable CreateRoundedBackground(Color color, int radiusDp)
        {
            var drawable = new GradientDrawable();
            drawable.SetColor(color);
            drawable.SetCornerRadius(radiusDp * _context.Resources?.DisplayMetrics?.Density ?? 8);
            return drawable;
        }
        public Button CreateStyledButton(string text, Color backgroundColor)
        {
            var button = new Button(_context)
            {
                Text = text,
                TextSize = 14
            };

            button.SetTextColor(Color.White);
            button.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Normal);
            button.SetPadding(16, 20, 16, 20);
            button.Background = CreateRoundedBackground(backgroundColor, 8);
            button.SetAllCaps(false);

            return button;
        }
        public TextView CreateDataLabel(string text)
        {
            var label = new TextView(_context)
            {
                Text = text,
                TextSize = 14,
                Gravity = GravityFlags.Center,
                LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1)
            };
            label.SetTextColor(TEXT_PRIMARY);
            label.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Normal);
            return label;
        }
        public EditText CreateStyledNumberField()
        {
            var editText = new EditText(_context)
            {
                InputType = Android.Text.InputTypes.ClassNumber,
                Text = "0",
                Gravity = GravityFlags.Center,
                LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1)
            };

            editText.SetTextColor(TEXT_PRIMARY);
            editText.SetTextSize(Android.Util.ComplexUnitType.Sp, 16);
            editText.SetPadding(16, 20, 16, 20);
            editText.Background = CreateRoundedBackground(TEXT_FIELD_BACKGROUND_COLOR, 8);

            var layoutParams = (LinearLayout.LayoutParams)editText.LayoutParameters;
            layoutParams.SetMargins(4, 0, 4, 0);

            return editText;
        }
        public Spinner CreateGateStatusSpinner()
        {
            var spinner = new Spinner(_context);
            spinner.SetPadding(16, 20, 16, 20);
            spinner.Background = CreateRoundedBackground(TEXT_FIELD_BACKGROUND_COLOR, 8);
            
            // Create options with blank first option instead of "null"
            var gateStatusOptions = new string[] { "", "gate up", "regate" };
            var adapter = new ArrayAdapter<string>(_context, Android.Resource.Layout.SimpleSpinnerItem, gateStatusOptions);
            adapter.SetDropDownViewResource(Android.Resource.Layout.SimpleSpinnerDropDownItem);
            spinner.Adapter = adapter;

            
            // Set the spinner to have the same layout weight as the input fields
            var spinnerParams = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1);
            spinnerParams.SetMargins(4, 0, 4, 0);
            spinner.LayoutParameters = spinnerParams;

            return spinner;
        }
    }
}