using AutoProxy.Windows;
using Xunit;

namespace AutoProxy.Windows.Tests;

public class CredentialManagerTests
{
    private static string NewTarget() => $"AutoProxy:Test:{Guid.NewGuid():N}";

    [Fact]
    public void Save_read_delete_round_trips_the_password()
    {
        var manager = new CredentialManager();
        var target = NewTarget();

        try
        {
            Assert.True(manager.SaveCredential(target, "alice", "s3cr3t-p@ss"));

            var read = manager.ReadCredential(target);
            Assert.NotNull(read);
            Assert.Equal("alice", read!.Value.Username);
            Assert.Equal("s3cr3t-p@ss", read.Value.Password);

            Assert.True(manager.DeleteCredential(target));
            Assert.Null(manager.ReadCredential(target));
        }
        finally
        {
            manager.DeleteCredential(target);
        }
    }

    [Fact]
    public void Oversized_password_falls_back_to_the_dpapi_store()
    {
        var manager = new CredentialManager();
        var target = NewTarget();
        var password = new string('P', 4000);

        try
        {
            Assert.True(manager.SaveCredential(target, "bob", password));

            var read = manager.ReadCredential(target);
            Assert.NotNull(read);
            Assert.Equal(password, read!.Value.Password);
        }
        finally
        {
            manager.DeleteCredential(target);
        }
    }

    [Fact]
    public void Missing_target_or_username_is_rejected()
    {
        var manager = new CredentialManager();

        Assert.False(manager.SaveCredential(string.Empty, "user", "pass"));
        Assert.False(manager.SaveCredential("target", string.Empty, "pass"));
        Assert.Null(manager.ReadCredential(string.Empty));
    }

    [Fact]
    public void Deleting_a_non_existent_credential_is_successful()
    {
        var manager = new CredentialManager();

        Assert.True(manager.DeleteCredential(NewTarget()));
    }
}
