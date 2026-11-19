// SPDX-License-Identifier: AGPL-3.0-only

using System.Windows;
using System.Windows.Controls;
using CouchSubs.Shell;

namespace CouchSubs.App.Views.Steps;

public partial class WelcomeStep : UserControl
{
    private bool loading;

    public WelcomeStep()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is not AppController controller) return;
            loading = true;
            (controller.UsesXbox ? Xbox : PlayStation).IsChecked = true;
            loading = false;
            ShowNeeds(controller.UsesXbox);
        };
    }

    private void SourceChanged(object sender, RoutedEventArgs e)
    {
        var xbox = sender == Xbox;
        ShowNeeds(xbox);
        if (!loading && DataContext is AppController controller) controller.UsesXbox = xbox;
    }

    private void ShowNeeds(bool xbox)
    {
        PlayStationNeeds.Visibility = xbox ? Visibility.Collapsed : Visibility.Visible;
        XboxNeeds.Visibility = xbox ? Visibility.Visible : Visibility.Collapsed;
    }
}
