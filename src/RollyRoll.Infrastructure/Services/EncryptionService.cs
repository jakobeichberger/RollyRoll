using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace RollyRoll.Infrastructure.Services;

/// <summary>
/// Provides AES-256-GCM encryption for user profile data at rest.
/// Encryption key is derived from the server master key using PBKDF2 (SHA-512, 600,000 iterations).
/// Each encryption operation uses a unique nonce (96-bit) and produces an authentication tag (128-bit)
/// for tamper detection. The encrypted output format is: [nonce(12)][tag(16)][ciphertext].
/// </summary>
public class EncryptionService
{
    private readonly ILogger<EncryptionService> _logger;
    private readonly byte[] _masterKey;

    /// <summary>Size of the AES-256 key in bytes.</summary>
    private const int KeySizeBytes = 32;

    /// <summary>Size of the AES-GCM nonce in bytes (96 bits).</summary>
    private const int NonceSizeBytes = 12;

    /// <summary>Size of the AES-GCM authentication tag in bytes (128 bits).</summary>
    private const int TagSizeBytes = 16;

    /// <summary>PBKDF2 iteration count for key derivation.</summary>
    private const int Pbkdf2Iterations = 600_000;

    /// <summary>Size of the PBKDF2 salt in bytes.</summary>
    private const int SaltSizeBytes = 16;

    /// <summary>Buffer size for streaming file encryption/decryption.</summary>
    private const int FileBufferSize = 64 * 1024; // 64 KB

    /// <summary>
    /// File header format: [salt(16)][nonce(12)][tag(16)][ciphertext...]
    /// The salt is used to derive the encryption key from the master key.
    /// </summary>
    private const int FileHeaderSize = SaltSizeBytes + NonceSizeBytes + TagSizeBytes;

    /// <summary>
    /// Initializes a new instance of the <see cref="EncryptionService"/> class.
    /// </summary>
    /// <param name="logger">Logger for encryption operations.</param>
    /// <param name="masterKey">
    /// Server master key used as the base for PBKDF2 key derivation.
    /// Each operation derives a unique encryption key using a random salt.
    /// </param>
    public EncryptionService(ILogger<EncryptionService> logger, string masterKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(masterKey);
        _logger = logger;
        _masterKey = Encoding.UTF8.GetBytes(masterKey);
    }

    /// <summary>
    /// Encrypts a file using AES-256-GCM and writes the result to the output path.
    /// The output file contains: [salt(16)][nonce(12)][tag(16)][ciphertext].
    /// </summary>
    /// <param name="inputPath">Path to the plaintext file to encrypt.</param>
    /// <param name="outputPath">Path where the encrypted file will be written.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="FileNotFoundException">Thrown when the input file does not exist.</exception>
    public async Task EncryptFileAsync(string inputPath, string outputPath, CancellationToken ct = default)
    {
        if (!File.Exists(inputPath))
            throw new FileNotFoundException($"Input file not found: {inputPath}");

        _logger.LogInformation("Encrypting file: {Input} -> {Output}", inputPath, outputPath);

        var plaintext = await File.ReadAllBytesAsync(inputPath, ct);

        // Generate a random salt and derive a unique key for this operation
        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var derivedKey = DeriveKey(salt);

        // Generate a random nonce
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);

        // Encrypt with AES-256-GCM
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSizeBytes];

        using (var aes = new AesGcm(derivedKey, TagSizeBytes))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        }

        // Write: [salt][nonce][tag][ciphertext]
        await using var outputStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await outputStream.WriteAsync(salt, ct);
        await outputStream.WriteAsync(nonce, ct);
        await outputStream.WriteAsync(tag, ct);
        await outputStream.WriteAsync(ciphertext, ct);
        await outputStream.FlushAsync(ct);

        _logger.LogInformation(
            "File encrypted successfully. Input: {InputSize} bytes, Output: {OutputSize} bytes.",
            plaintext.Length, SaltSizeBytes + NonceSizeBytes + TagSizeBytes + ciphertext.Length);

        // Clear sensitive data from memory
        CryptographicOperations.ZeroMemory(derivedKey);
        CryptographicOperations.ZeroMemory(plaintext);
    }

    /// <summary>
    /// Decrypts a file that was encrypted with <see cref="EncryptFileAsync"/> and writes the plaintext to the output path.
    /// Verifies the authentication tag to detect tampering.
    /// </summary>
    /// <param name="inputPath">Path to the encrypted file.</param>
    /// <param name="outputPath">Path where the decrypted file will be written.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="FileNotFoundException">Thrown when the input file does not exist.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the file is too small to be a valid encrypted file.</exception>
    /// <exception cref="CryptographicException">Thrown when decryption fails (wrong key or tampered data).</exception>
    public async Task DecryptFileAsync(string inputPath, string outputPath, CancellationToken ct = default)
    {
        if (!File.Exists(inputPath))
            throw new FileNotFoundException($"Encrypted file not found: {inputPath}");

        _logger.LogInformation("Decrypting file: {Input} -> {Output}", inputPath, outputPath);

        var encryptedData = await File.ReadAllBytesAsync(inputPath, ct);

        if (encryptedData.Length < FileHeaderSize)
            throw new InvalidOperationException(
                $"File is too small to be a valid encrypted file ({encryptedData.Length} bytes, minimum {FileHeaderSize}).");

        // Read: [salt(16)][nonce(12)][tag(16)][ciphertext...]
        var salt = encryptedData.AsSpan(0, SaltSizeBytes).ToArray();
        var nonce = encryptedData.AsSpan(SaltSizeBytes, NonceSizeBytes).ToArray();
        var tag = encryptedData.AsSpan(SaltSizeBytes + NonceSizeBytes, TagSizeBytes).ToArray();
        var ciphertext = encryptedData.AsSpan(FileHeaderSize).ToArray();

        // Derive the same key using the stored salt
        var derivedKey = DeriveKey(salt);

        // Decrypt and verify authentication tag
        var plaintext = new byte[ciphertext.Length];

        try
        {
            using var aes = new AesGcm(derivedKey, TagSizeBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }
        catch (CryptographicException ex)
        {
            _logger.LogError(ex, "Decryption failed for file {Path}. The data may be corrupted or the master key is incorrect.", inputPath);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derivedKey);
        }

        await File.WriteAllBytesAsync(outputPath, plaintext, ct);

        _logger.LogInformation(
            "File decrypted successfully. Input: {InputSize} bytes, Output: {OutputSize} bytes.",
            encryptedData.Length, plaintext.Length);

        CryptographicOperations.ZeroMemory(plaintext);
    }

    /// <summary>
    /// Encrypts a string using AES-256-GCM and returns the result as a Base64-encoded string.
    /// The format is: Base64([salt(16)][nonce(12)][tag(16)][ciphertext]).
    /// </summary>
    /// <param name="plaintext">The plaintext string to encrypt.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Base64-encoded encrypted string containing salt, nonce, tag, and ciphertext.</returns>
    public Task<string> EncryptStringAsync(string plaintext, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);

        // Generate random salt and derive key
        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var derivedKey = DeriveKey(salt);

        // Generate random nonce
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);

        // Encrypt
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSizeBytes];

        using (var aes = new AesGcm(derivedKey, TagSizeBytes))
        {
            aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);
        }

        CryptographicOperations.ZeroMemory(derivedKey);
        CryptographicOperations.ZeroMemory(plaintextBytes);

        // Combine: [salt][nonce][tag][ciphertext]
        var result = new byte[SaltSizeBytes + NonceSizeBytes + TagSizeBytes + ciphertext.Length];
        Buffer.BlockCopy(salt, 0, result, 0, SaltSizeBytes);
        Buffer.BlockCopy(nonce, 0, result, SaltSizeBytes, NonceSizeBytes);
        Buffer.BlockCopy(tag, 0, result, SaltSizeBytes + NonceSizeBytes, TagSizeBytes);
        Buffer.BlockCopy(ciphertext, 0, result, SaltSizeBytes + NonceSizeBytes + TagSizeBytes, ciphertext.Length);

        return Task.FromResult(Convert.ToBase64String(result));
    }

    /// <summary>
    /// Decrypts a Base64-encoded string that was encrypted with <see cref="EncryptStringAsync"/>.
    /// Verifies the authentication tag to detect tampering.
    /// </summary>
    /// <param name="encryptedBase64">The Base64-encoded encrypted string.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The decrypted plaintext string.</returns>
    /// <exception cref="CryptographicException">Thrown when decryption fails (wrong key or tampered data).</exception>
    public Task<string> DecryptStringAsync(string encryptedBase64, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encryptedBase64);

        var encryptedData = Convert.FromBase64String(encryptedBase64);

        if (encryptedData.Length < FileHeaderSize)
            throw new InvalidOperationException(
                $"Encrypted data is too short ({encryptedData.Length} bytes, minimum {FileHeaderSize}).");

        // Read: [salt(16)][nonce(12)][tag(16)][ciphertext...]
        var salt = new byte[SaltSizeBytes];
        var nonce = new byte[NonceSizeBytes];
        var tag = new byte[TagSizeBytes];
        var ciphertext = new byte[encryptedData.Length - FileHeaderSize];

        Buffer.BlockCopy(encryptedData, 0, salt, 0, SaltSizeBytes);
        Buffer.BlockCopy(encryptedData, SaltSizeBytes, nonce, 0, NonceSizeBytes);
        Buffer.BlockCopy(encryptedData, SaltSizeBytes + NonceSizeBytes, tag, 0, TagSizeBytes);
        Buffer.BlockCopy(encryptedData, FileHeaderSize, ciphertext, 0, ciphertext.Length);

        var derivedKey = DeriveKey(salt);
        var plaintext = new byte[ciphertext.Length];

        try
        {
            using var aes = new AesGcm(derivedKey, TagSizeBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }
        catch (CryptographicException ex)
        {
            _logger.LogError(ex, "String decryption failed. The data may be corrupted or the master key is incorrect.");
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derivedKey);
        }

        var result = Encoding.UTF8.GetString(plaintext);
        CryptographicOperations.ZeroMemory(plaintext);

        return Task.FromResult(result);
    }

    /// <summary>
    /// Derives a 256-bit encryption key from the master key and a salt using PBKDF2 with SHA-512.
    /// </summary>
    /// <param name="salt">Random salt for key derivation (must be unique per encryption operation).</param>
    /// <returns>A 32-byte derived key suitable for AES-256.</returns>
    private byte[] DeriveKey(byte[] salt)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            _masterKey,
            salt,
            Pbkdf2Iterations,
            HashAlgorithmName.SHA512,
            KeySizeBytes);
    }
}
