using System.IO;
using System.Text.Json;
using SQLST.Contracts;

namespace SQLST.Infrastructure;

/// <summary>
/// Profilleri %APPDATA%\SQLST\profiles.json dosyasında tutar (02-mimari §1).
/// Parola alanı buraya gelmeden önce ISecretProtector ile şifrelenmiş olmalıdır.
///
/// Yolu <see cref="UygulamaVeriYolu"/> verir: 0.5.0 ve öncesinde klasör adı MiniSSMS'ti,
/// göç oradadır.
/// </summary>
public sealed class JsonProfileStore : IProfileStore
{
    private static readonly JsonSerializerOptions JsonAyar = new() { WriteIndented = true };
    private readonly string _dosyaYolu;
    private readonly SemaphoreSlim _kilit = new(1, 1);

    public JsonProfileStore() : this(UygulamaVeriYolu.ProfilDosyasi)
    {
    }

    public JsonProfileStore(string dosyaYolu) => _dosyaYolu = dosyaYolu;

    public async Task<IReadOnlyList<ConnectionProfile>> GetAllAsync(CancellationToken ct = default)
    {
        await _kilit.WaitAsync(ct);
        try
        {
            return await OkuAsync(ct);
        }
        finally
        {
            _kilit.Release();
        }
    }

    public async Task SaveAsync(ConnectionProfile profil, CancellationToken ct = default)
    {
        await _kilit.WaitAsync(ct);
        try
        {
            List<ConnectionProfile> liste = await OkuAsync(ct);
            int i = liste.FindIndex(p => p.Id == profil.Id);
            if (i >= 0) liste[i] = profil;
            else liste.Add(profil);
            await YazAsync(liste, ct);
        }
        finally
        {
            _kilit.Release();
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await _kilit.WaitAsync(ct);
        try
        {
            List<ConnectionProfile> liste = await OkuAsync(ct);
            liste.RemoveAll(p => p.Id == id);
            await YazAsync(liste, ct);
        }
        finally
        {
            _kilit.Release();
        }
    }

    private async Task<List<ConnectionProfile>> OkuAsync(CancellationToken ct)
    {
        if (!File.Exists(_dosyaYolu))
            return [];
        await using FileStream fs = File.OpenRead(_dosyaYolu);
        return await JsonSerializer.DeserializeAsync<List<ConnectionProfile>>(fs, JsonAyar, ct) ?? [];
    }

    private async Task YazAsync(List<ConnectionProfile> liste, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dosyaYolu)!);
        await using FileStream fs = File.Create(_dosyaYolu);
        await JsonSerializer.SerializeAsync(fs, liste, JsonAyar, ct);
    }
}
