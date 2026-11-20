using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using Lumen.Windows.Models;
using Lumen.Windows.Services;
using Lumen.Windows.ViewModels;
using Xunit;

namespace Lumen.Windows.Tests;

public sealed class SendCommandTests
{
    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static MainViewModel CreateViewModel()
    {
        var services = new AppServices();
        return services.MainViewModel;
    }

    private static PeerDevice Peer(bool serverOnline) => new()
    {
        Id = Guid.NewGuid(),
        Name = "OPPO PLG110",
        Platform = "android",
        ServerOnline = serverOnline,
        IsKnownOnServer = true,
        ServerIp = "240e:40d:102:a0a1::1",
        ServerIpFamily = "IPv6"
    };

    [Fact]
    public void OnlinePeer_EnablesEverySendCommand()
    {
        OnSta(() =>
        {
            var viewModel = CreateViewModel();
            var peer = Peer(serverOnline: true);

            Assert.True(viewModel.SendFilesCommand.CanExecute(peer));
            Assert.True(viewModel.SendImageCommand.CanExecute(peer));
            Assert.True(viewModel.SendClipboardCommand.CanExecute(peer));
            Assert.True(viewModel.SendTextCommand.CanExecute(peer));
        });
    }

    [Fact]
    public void OfflinePeer_DisablesFileCommands()
    {
        OnSta(() =>
        {
            var viewModel = CreateViewModel();
            var peer = Peer(serverOnline: false);

            Assert.False(viewModel.SendFilesCommand.CanExecute(peer));
            Assert.False(viewModel.SendImageCommand.CanExecute(peer));
            Assert.False(viewModel.SendClipboardCommand.CanExecute(peer));
        });
    }

    [Fact]
    public void ComposeSend_TracksTypedText()
    {
        OnSta(() =>
        {
            var viewModel = CreateViewModel();

            Assert.False(viewModel.CanSendCompose);
            viewModel.ComposeText = "   ";
            Assert.False(viewModel.CanSendCompose);

            viewModel.ComposeText = "你好，电脑";
            Assert.True(viewModel.CanSendCompose);
            Assert.True(viewModel.ConfirmComposeCommand.CanExecute(null));
        });
    }

    [Fact]
    public void ActiveSend_DoesNotBlockOtherSends()
    {
        OnSta(() =>
        {
            var viewModel = CreateViewModel();
            var peer = Peer(serverOnline: true);
            var field = typeof(MainViewModel).GetField(
                "_isSending",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(field);
            field.SetValue(viewModel, true);

            Assert.True(viewModel.SendFilesCommand.CanExecute(peer));
            Assert.True(viewModel.SendImageCommand.CanExecute(peer));
        });
    }

    [Fact]
    public void ThemeSwitch_UpdatesPaletteAndState()
    {
        OnSta(() =>
        {
            ThemeManager.Apply(ThemeManager.LightMode);
            Assert.True(ThemeManager.IsLight);

            ThemeManager.Apply(ThemeManager.DarkMode);
            Assert.False(ThemeManager.IsLight);

            var dispatcher = Dispatcher.CurrentDispatcher;
            Assert.NotNull(dispatcher);
        });
    }
}
