using Vino.BridgeContract;

namespace Vino.BridgeContract.Tests;

public sealed class BridgeAuthenticationTests
{
    [Fact]
    public void ClientProof_VerifiesOnlyForMatchingSecretAndTranscript()
    {
        var secret = BridgeSecret.Generate();
        var otherSecret = BridgeSecret.Generate();
        var clientNonce = BridgeAuthenticator.CreateNonce();
        var serverNonce = BridgeAuthenticator.CreateNonce();
        var proof = BridgeAuthenticator.CreateClientProof(
            secret,
            "vino-test",
            clientNonce,
            serverNonce,
            "client-1",
            "server-1");

        Assert.True(
            BridgeAuthenticator.VerifyClientProof(
                secret,
                "vino-test",
                clientNonce,
                serverNonce,
                "client-1",
                "server-1",
                proof));
        Assert.False(
            BridgeAuthenticator.VerifyClientProof(
                otherSecret,
                "vino-test",
                clientNonce,
                serverNonce,
                "client-1",
                "server-1",
                proof));
        Assert.False(
            BridgeAuthenticator.VerifyClientProof(
                secret,
                "vino-other",
                clientNonce,
                serverNonce,
                "client-1",
                "server-1",
                proof));
        Assert.False(
            BridgeAuthenticator.VerifyClientProof(
                secret,
                "vino-test",
                clientNonce,
                serverNonce,
                "client-1",
                "server-2",
                proof));
    }

    [Fact]
    public void ServerProof_IsDomainSeparatedAndBoundToServerIdentity()
    {
        var secret = BridgeSecret.Generate();
        var clientNonce = BridgeAuthenticator.CreateNonce();
        var serverNonce = BridgeAuthenticator.CreateNonce();
        var serverProof = BridgeAuthenticator.CreateServerProof(
            secret,
            "vino-test",
            clientNonce,
            serverNonce,
            "server-1");
        var clientProof = BridgeAuthenticator.CreateClientProof(
            secret,
            "vino-test",
            clientNonce,
            serverNonce,
            "client-1",
            "server-1");

        Assert.True(
            BridgeAuthenticator.VerifyServerProof(
                secret,
                "vino-test",
                clientNonce,
                serverNonce,
                "server-1",
                serverProof));
        Assert.False(
            BridgeAuthenticator.VerifyServerProof(
                secret,
                "vino-test",
                clientNonce,
                serverNonce,
                "server-2",
                serverProof));
        Assert.False(
            BridgeAuthenticator.VerifyServerProof(
                secret,
                "vino-test",
                clientNonce,
                serverNonce,
                "server-1",
                clientProof));
    }

    [Fact]
    public void Secret_RoundTripsThroughBase64()
    {
        var secret = BridgeSecret.Generate();
        var restored = BridgeSecret.FromBase64(secret.ExportBase64());
        var clientNonce = BridgeAuthenticator.CreateNonce();
        var serverNonce = BridgeAuthenticator.CreateNonce();
        var proof = BridgeAuthenticator.CreateServerProof(
            secret,
            "vino-test",
            clientNonce,
            serverNonce,
            "server-1");

        Assert.True(
            BridgeAuthenticator.VerifyServerProof(
                restored,
                "vino-test",
                clientNonce,
                serverNonce,
                "server-1",
                proof));
    }
}
