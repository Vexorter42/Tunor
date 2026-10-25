using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Tunor.Views;

public enum PasswordChoice { Cancel, Password, NoPassword }

/// <summary>
/// Asks for a password: twice and with a way out of having one, when a backup is being made;
/// once, when a locked backup is being opened.
/// </summary>
public partial class PasswordDialog : DialogPanel
{
    private readonly bool _setting;

    public PasswordChoice Choice { get; private set; } = PasswordChoice.Cancel;
    public string Password => Pw.Password;

    /// <summary>A backup is being made: a new password, or none.</summary>
    public static PasswordDialog ForSaving() => new(true, null);

    /// <summary>A locked backup is being opened. <paramref name="problem"/> is what went wrong the last time, if it did.</summary>
    public static PasswordDialog ForOpening(string? problem) => new(false, problem);

    private PasswordDialog(bool setting, string? problem)
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
            RepeatPanel.Visibility = Visibility.Collapsed;
            BtnNone.Visibility = Visibility.Collapsed;
            BtnOk.Content = "Открыть";
            Show(problem);
        }
    }

    public override UIElement? InitialFocus => Pw;

    private void Show(string? problem)
    {
        ProblemText.Text = problem ?? "";
        ProblemText.Visibility = string.IsNullOrEmpty(problem) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Pw.Password.Length == 0)
        {
            Show(_setting ? "Введите пароль, либо выберите «Без пароля»." : "Введите пароль.");
            return;
        }
        if (_setting && Pw.Password != Pw2.Password)
        {
            Show("Пароли не совпадают.");
            return;
        }
        Choice = PasswordChoice.Password;
        Close();
    }

    private void None_Click(object sender, RoutedEventArgs e)
    {
        Choice = PasswordChoice.NoPassword;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Pw_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (_setting && sender == Pw) Pw2.Focus();
        else Ok_Click(sender, e);
    }
}
