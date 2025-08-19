using Avalonia.Controls;

namespace HoyoversePckPlayer;

public partial class Progress : Window
{
    public Progress()
    {
        InitializeComponent();
        Closing += (_, args) =>
        {
            if (!args.IsProgrammatic)
                args.Cancel = true;
        };
    }
}

