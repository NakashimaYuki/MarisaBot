using Marisa.BotDriver.Plugin;
using Marisa.Plugin.Shared.Dialog;
using NUnit.Framework;

namespace Marisa.BotDriver.Test;

using TKey = (long? GroupId, long? SenderId);

public class DialogManagerTest
{
    [Test]
    public void RemoveDialog_With_Matching_Handler_Removes_Handler_And_Source()
    {
        TKey key = (123, 457);
        Dialog.MessageHandler handler = _ => Task.FromResult(MarisaPluginTaskState.CompletedTask);
        var source = new MarisaPluginBase();

        DialogManager.RemoveDialog(key);
        try
        {
            Assert.That(DialogManager.TryAddDialog(key, handler, source), Is.True);
            Assert.That(DialogManager.GetSourcePlugin(key), Is.SameAs(source));

            Assert.That(DialogManager.RemoveDialog(key, handler), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(DialogManager.ContainsDialog(key), Is.False);
                Assert.That(DialogManager.TryGetDialog(key, out var removedHandler), Is.False);
                Assert.That(removedHandler, Is.Null);
                Assert.That(DialogManager.GetSourcePlugin(key), Is.Null);
                Assert.That(DialogManager.RemoveDialog(key, handler), Is.False);
            });
        }
        finally
        {
            DialogManager.RemoveDialog(key);
        }
    }

    [Test]
    public void RemoveDialog_With_Old_Handler_Preserves_Newer_Handler_And_Source()
    {
        TKey key = (123, 458);
        Dialog.MessageHandler oldHandler = _ => Task.FromResult(MarisaPluginTaskState.ToBeContinued);
        Dialog.MessageHandler newHandler = _ => Task.FromResult(MarisaPluginTaskState.CompletedTask);
        var oldSource = new MarisaPluginBase();
        var newSource = new MarisaPluginBase();

        DialogManager.RemoveDialog(key);
        try
        {
            Assert.That(DialogManager.TryAddDialog(key, oldHandler, oldSource), Is.True);
            Assert.That(DialogManager.RemoveDialog(key, oldHandler), Is.True);
            Assert.That(DialogManager.TryAddDialog(key, newHandler, newSource), Is.True);

            Assert.That(DialogManager.RemoveDialog(key, oldHandler), Is.False);
            Assert.Multiple(() =>
            {
                Assert.That(DialogManager.ContainsDialog(key), Is.True);
                Assert.That(DialogManager.TryGetDialog(key, out var remainingHandler), Is.True);
                Assert.That(remainingHandler, Is.SameAs(newHandler));
                Assert.That(DialogManager.GetSourcePlugin(key), Is.SameAs(newSource));
            });

            Assert.That(DialogManager.RemoveDialog(key, newHandler), Is.True);
            Assert.That(DialogManager.GetSourcePlugin(key), Is.Null);
        }
        finally
        {
            DialogManager.RemoveDialog(key);
        }
    }

    [Test]
    public void TryRestoreDialog_Should_Not_Override_Newer_Handler()
    {
        TKey key = (123, 456);
        Dialog.MessageHandler handler1 = _ => Task.FromResult(Marisa.BotDriver.Plugin.MarisaPluginTaskState.NoResponse);
        Dialog.MessageHandler handler2 = _ => Task.FromResult(Marisa.BotDriver.Plugin.MarisaPluginTaskState.CompletedTask);

        DialogManager.RemoveDialog(key);

        Assert.That(DialogManager.TryAddDialog(key, handler1), Is.True);
        DialogManager.RemoveDialog(key);

        Assert.That(DialogManager.TryAddDialog(key, handler2), Is.True);
        Assert.That(DialogManager.TryRestoreDialog(key, handler1, null), Is.False);
        Assert.That(DialogManager.TryGetDialog(key, out var restoredHandler), Is.True);
        Assert.That(restoredHandler, Is.SameAs(handler2));

        DialogManager.RemoveDialog(key);
    }
}
