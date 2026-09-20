namespace AutoProxy.Core.Abstractions;

public interface ICredentialManager
{
    bool SaveCredential(string target, string username, string password);
    (string Username, string Password)? ReadCredential(string target);
    bool DeleteCredential(string target);
}