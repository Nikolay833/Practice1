using System.Globalization;
using System.Windows;
using System.Windows.Input;

namespace Dropshelf.Settings;

/// <summary>A small dark dialog that asks for one whole number (the "Resize to max width" size). Enter accepts, Esc cancels.</summary>
public partial class NumberPromptWindow : Window
{
    private readonly int _min;
    private readonly int _max;

    public NumberPromptWindow(string title, string hint, int initial, int min, int max)
    {
        InitializeComponent();
        _min = min;
        _max = max;
        TitleText.Text = title;
        HintText.Text = hint;
        Input.Text = initial.ToString(CultureInfo.CurrentCulture);
        Loaded += (_, _) =>
        {
            Activate();
            Input.Focus();
            Input.SelectAll();
        };
    }

    /// <summary>The number entered (valid only when the dialog result is true).</summary>
    public int Value { get; private set; }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(Input.Text.Trim(), NumberStyles.None, CultureInfo.CurrentCulture, out int value) && value >= _min && value <= _max)
        {
            Value = value;
            DialogResult = true;
        }
        else
        {
            HintText.Text = $"Enter a whole number from {_min} to {_max}.";
            Input.Focus();
            Input.SelectAll();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Ok_Click(sender, e);
            e.Handled = true;
        }
    }
}
