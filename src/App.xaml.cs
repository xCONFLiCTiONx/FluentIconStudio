using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FluentIconStudio;

public partial class App : Application
{
    private void TextBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox textBox && Mouse.LeftButton != MouseButtonState.Pressed)
        {
            textBox.SelectAll();
        }
    }

    private void Slider_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Prevent sliders from changing values on mouse wheel scroll
        e.Handled = false;
    }
}
