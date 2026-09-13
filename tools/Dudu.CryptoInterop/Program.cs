using System.Security.Cryptography;
using System.Text.Json;
using Dudu.Infrastructure.Crypto;

namespace Dudu.CryptoInterop;

/// <summary>
/// A tiny CLI used only to prove cross-runtime interop for the version-one encrypted-note
/// protocol: it decrypts an envelope produced by any implementation (including the TypeScript
/// one under relay/) using the production <see cref="EnvelopeCrypto"/> decrypt path. Never used
/// by the shipping desktop app.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            return args.FirstOrDefault()?.ToLowerInvariant() switch
            {
                "decrypt" => Decrypt(args[1..]),
                _ => Usage(),
            };
        }
        catch (Exception exception) when (exception is EnvelopeValidationException or CryptographicException or IOException or JsonException or ArgumentException)
        {
            Console.Error.WriteLine($"ERROR: {exception.Message}");
            return 1;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: Dudu.CryptoInterop decrypt --private <pkcs8-file> --envelope <json-file>");
        return 2;
    }

    private static int Decrypt(string[] args)
    {
        var privatePath = RequiredOption(args, "--private");
        var envelopePath = RequiredOption(args, "--envelope");

        var privateKeyPkcs8 = File.ReadAllBytes(privatePath);
        var envelopeJson = File.ReadAllBytes(envelopePath);

        var envelope = JsonSerializer.Deserialize(envelopeJson, EnvelopeJsonContext.Default.EncryptedEnvelope)
            ?? throw new ArgumentException("Envelope JSON deserialized to null.");

        var payload = EnvelopeCrypto.Decrypt(envelope, privateKeyPkcs8);

        Console.Out.Write(JsonSerializer.Serialize(payload, EnvelopeJsonContext.Default.RemoteMessagePayload));
        return 0;
    }

    private static string RequiredOption(string[] args, string option)
    {
        var index = Array.IndexOf(args, option);
        if (index < 0 || index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
        {
            throw new ArgumentException($"Missing required option {option}.");
        }

        return args[index + 1];
    }
}
