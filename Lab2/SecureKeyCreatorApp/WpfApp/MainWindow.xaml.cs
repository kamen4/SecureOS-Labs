using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WpfApp;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        SetButtonEnabled();
    }

    private void TextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SetButtonEnabled();
        
        if (!Directory.Exists(TextBoxFolder.Text))
        {
            TextBoxFolderBorder.BorderBrush = Brushes.Red;
        }
        else
        {
            TextBoxFolderBorder.BorderBrush = Brushes.Transparent;
        }
    }

    private void TextBoxName_TextChanged(object sender, TextChangedEventArgs e)
    {
        SetButtonEnabled();
        
        if (string.IsNullOrWhiteSpace(TextBoxName.Text))
        {
            TextBoxNameBorder.BorderBrush = Brushes.Red;
        }
        else
        {
            TextBoxNameBorder.BorderBrush = Brushes.Transparent;
        }
    }

    private void RichTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SetButtonEnabled();
    }

    private void SetButtonEnabled()
    {
        if (Button is null)
        {
            return;
        }    

        string textFolder = TextBoxFolder?.Text;
        string textFileName = TextBoxName?.Text;
        string textSecret = TextBoxSecret?.Text;

        Button.IsEnabled = 
            !string.IsNullOrWhiteSpace(textFolder) &&
            !string.IsNullOrWhiteSpace(textFileName) &&
            !string.IsNullOrWhiteSpace(textSecret) &&
            Directory.Exists(textFolder);
    }

    private async void Button_Click(object sender, RoutedEventArgs e)
    {
        string textFolder = TextBoxFolder.Text;
        string textFileName = TextBoxName.Text;
        string textSecret = TextBoxSecret.Text;

        Button.IsEnabled = false;

        try
        {
            await AppGenerator.GenerateAsync(textFolder, textFileName, textSecret, true, "password");
            MessageBox.Show($"Created {textFileName}.exe at {textFolder}.\n{textSecret.Length} symbols", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Button.IsEnabled = true;
        }
    }
}
