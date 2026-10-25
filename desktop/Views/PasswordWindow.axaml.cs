using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Tunor.Desktop.Views;

public enum PasswordChoice { Cancel, Password, NoPassword }

/// <summary>
/// Asks for a password: twice and with a way out of having one, when a backup is being made;
/// once, when a locked backup is being opened.
/// </summary>
public partial class PasswordWindow : DialogPanel
{
    private readonly bool _setting;

    public PasswordChoice Choice { get; private set; } = PasswordChoice.Cancel;
    public string Password => Pw.Text ?? "";

    public PasswordWindow() : this(true, null) { }

    /// <summary>A backup is being made: a new password, or none.</summary>
    public static PasswordWindow ForSaving() => new(true, null);

    /// <summary>A locked backup is being opened. <paramref name="problem"/> is what went wrong the last time, if it did.</summary>
    public static PasswordWindow ForOpening(string? problem) => new(false, problem);

    private PasswordWindow(bool setting, string? problem)
    {
        InitializeComponent();
        _setting = setting;
        if (setting)
        {
            Title = "Сохранить настройки";
            HeadText.Text = "Защитить файл паролем?";
            BodyText.Text = "В копии лежат ключи туннелей и ссылки подписок — это как пароли от ваших серверов. "
                          + "С паролем файл не прочитать без него; без пароля это обычный архив, его лучше хранить только у себя. "
                          + "Забытый пароль не восстановить.";
            BtnOk.Content = "Сохранить";
        }
        else
        {
            Title = "Пароль";
            HeadText.Text = "Файл защищён паролем";
            BodyText.Text = "Введите пароль, который был задан, когда копию сохраняли.";
            RepeatPanel.IsVisible = false;
            BtnNone.IsVisible = false;
            BtnOk.Content = "Открыть";
            Show(problem);
        }
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => Pw.Focus());
    }

    private void Show(string? problem)
    {
        ProblemText.Text = problem ?? "";
        ProblemText.IsVisible = !string.IsNullOrEmpty(problem);
    }

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        if (Password.Length == 0)
        {
            Show(_setting ? "Введите пароль, либо выберите «Без пароля»." : "Введите пароль.");
            return;
        }
        if (_setting && Password != (Pw2.Text ?? ""))
        {
            Show("Пароли не совпадают.");
            return;
        }
        Choice = PasswordChoice.Password;
        Close();
    }

    private void None_Click(object? sender, RoutedEventArgs e)
    {
        Choice = PasswordChoice.NoPassword;
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();

    private void Pw_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (_setting && sender == Pw) Pw2.Focus();
        else Ok_Click(sender, e);
    }
}
