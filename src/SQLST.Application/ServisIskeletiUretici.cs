using System.Text;
using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>
/// SP'den WCF servis iskeleti üretici (v14-S4, kullanıcı senaryosu: "WCF servisim var, DB'deki
/// SP'yi kullanıyor" — o sarmalama kodunun otomasyonu). SAF: SP imzasından
/// ServiceContract + implementasyon + ADO.NET çağrısı içeren TEK C# dosyası metni üretir;
/// sekmede açılır, projeye kopyalanır (hiçbir şey derlenmez/çalıştırılmaz — v8 sihirbaz ilkesi).
/// OUTPUT parametreleri gerçek <c>out</c> parametre olarak üretilir; dönüş ilk sonuç kümesi
/// (DataTable) — SP'nin sonuç şekli metadatadan bilinemez, pragmatik seçim budur ve yorumda
/// söylenir. Yalnız SQL Server hedeflenir (menü de yalnız orada görünür).
/// </summary>
public static class ServisIskeletiUretici
{
    public static string Uret(SemaNesnesi sp)
    {
        string sinif = GecerliAd(sp.Ad);
        var p = sp.Parametreler;
        var sb = new StringBuilder();

        sb.AppendLine($"// {sp.TamAd} için WCF servis iskeleti — SQLST üretti.");
        sb.AppendLine("// Kullanım: projenize kopyalayın, ad alanını ve bağlantı dizesini düzenleyin.");
        sb.AppendLine("// Dönüş DataTable'dır (SP'nin İLK sonuç kümesi) — kendi DataContract");
        sb.AppendLine("// modelinize eşlemek isterseniz Fill sonrası satırları dönüştürün.");
        sb.AppendLine("using System.Data;");
        sb.AppendLine("using System.Data.SqlClient;");
        sb.AppendLine("using System.ServiceModel;");
        sb.AppendLine();
        sb.AppendLine("[ServiceContract(Namespace = \"http://tempuri.org/\")]");
        sb.AppendLine($"public interface I{sinif}Servisi");
        sb.AppendLine("{");
        sb.AppendLine("    [OperationContract]");
        sb.AppendLine($"    DataTable {sinif}({Imza(p)});");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine($"public class {sinif}Servisi : I{sinif}Servisi");
        sb.AppendLine("{");
        sb.AppendLine("    private const string BaglantiDizesi = \"TODO: bağlantı dizesi\";");
        sb.AppendLine();
        sb.AppendLine($"    public DataTable {sinif}({Imza(p)})");
        sb.AppendLine("    {");
        sb.AppendLine("        using var baglanti = new SqlConnection(BaglantiDizesi);");
        sb.AppendLine($"        using var komut = new SqlCommand(\"{sp.TamAdKoseli}\", baglanti)");
        sb.AppendLine("        {");
        sb.AppendLine("            CommandType = CommandType.StoredProcedure,");
        sb.AppendLine("        };");
        foreach (SemaParametresi prm in p)
        {
            string cad = CamelAd(prm.Ad);
            if (prm.CikisMi)
            {
                sb.AppendLine($"        var p{cad} = komut.Parameters.Add(\"{prm.Ad}\", SqlDbType.{SqlDbTipi(prm.Tip)});");
                sb.AppendLine($"        p{cad}.Direction = ParameterDirection.Output;");
            }
            else
            {
                sb.AppendLine($"        komut.Parameters.AddWithValue(\"{prm.Ad}\", (object){cad} ?? DBNull.Value);");
            }
        }

        sb.AppendLine();
        sb.AppendLine("        var tablo = new DataTable();");
        sb.AppendLine("        using (var uyarlayici = new SqlDataAdapter(komut))");
        sb.AppendLine("            uyarlayici.Fill(tablo); // bağlantıyı kendisi açar/kapatır");
        foreach (SemaParametresi prm in p.Where(x => x.CikisMi))
        {
            string cad = CamelAd(prm.Ad);
            sb.AppendLine($"        {cad} = p{cad}.Value is DBNull ? default : ({CsTip(prm.Tip)})p{cad}.Value;");
        }

        sb.AppendLine("        return tablo;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string Imza(IReadOnlyList<SemaParametresi> parametreler)
        => string.Join(", ", parametreler.Select(p =>
            p.CikisMi ? $"out {CsTip(p.Tip)} {CamelAd(p.Ad)}" : $"{CsTip(p.Tip)} {CamelAd(p.Ad)}"));

    /// <summary>"@MusteriId" → "musteriId" (C# parametre adı).</summary>
    private static string CamelAd(string ad)
    {
        string temiz = ad.TrimStart('@');
        return temiz.Length == 0 ? "p" : char.ToLowerInvariant(temiz[0]) + temiz[1..];
    }

    /// <summary>Sınıf adı: harf/rakam dışını at, rakamla başlıyorsa öne Sp koy.</summary>
    private static string GecerliAd(string ad)
    {
        string temiz = new([.. ad.Where(char.IsLetterOrDigit)]);
        if (temiz.Length == 0)
            temiz = "Sp";
        return char.IsDigit(temiz[0]) ? "Sp" + temiz : char.ToUpperInvariant(temiz[0]) + temiz[1..];
    }

    /// <summary>SQL tipi → C# tipi ("nvarchar(100)" gibi parantezli gelir — kökü alınır).</summary>
    internal static string CsTip(string sqlTip)
    {
        string kok = sqlTip.Split('(')[0].Trim().ToLowerInvariant();
        return kok switch
        {
            "int" => "int",
            "bigint" => "long",
            "smallint" => "short",
            "tinyint" => "byte",
            "bit" => "bool",
            "decimal" or "numeric" or "money" or "smallmoney" => "decimal",
            "float" => "double",
            "real" => "float",
            "date" or "datetime" or "datetime2" or "smalldatetime" => "DateTime",
            "datetimeoffset" => "DateTimeOffset",
            "time" => "TimeSpan",
            "uniqueidentifier" => "Guid",
            "varbinary" or "binary" or "image" or "timestamp" or "rowversion" => "byte[]",
            _ => "string",
        };
    }

    /// <summary>SQL tipi → SqlDbType üyesi (OUTPUT parametre tanımı için).</summary>
    private static string SqlDbTipi(string sqlTip)
    {
        string kok = sqlTip.Split('(')[0].Trim().ToLowerInvariant();
        return kok switch
        {
            "int" => "Int",
            "bigint" => "BigInt",
            "smallint" => "SmallInt",
            "tinyint" => "TinyInt",
            "bit" => "Bit",
            "decimal" or "numeric" => "Decimal",
            "money" => "Money",
            "smallmoney" => "SmallMoney",
            "float" => "Float",
            "real" => "Real",
            "date" => "Date",
            "datetime" => "DateTime",
            "datetime2" => "DateTime2",
            "smalldatetime" => "SmallDateTime",
            "datetimeoffset" => "DateTimeOffset",
            "time" => "Time",
            "uniqueidentifier" => "UniqueIdentifier",
            "varbinary" => "VarBinary",
            "binary" => "Binary",
            "varchar" => "VarChar",
            "char" => "Char",
            "nchar" => "NChar",
            _ => "NVarChar",
        };
    }
}
