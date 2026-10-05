namespace SQLST.Contracts;

/// <summary>
/// Dosya tip tahmininden motor kolon tipine eşleme (v13-S4, "yeni tablo" yolu) — SAF.
/// Motor farkları çoklu-motor kuralı gereği DAĞITILMAZ, tek düzenli haritada durur
/// (motor kimlikleri <see cref="ILehce.MotorId"/> ile birebir). Metin boyu örneklemdeki en
/// uzun değerden BASAMAKLA yuvarlanır (50/100/255/1000/4000; örneklem yanılabilir — dar
/// kesmektense genişletilir, sınırı aşan max/text sınıfına düşer). Kolonlar HEP NULL izinli
/// üretilir: 200 satırlık önizleme örneklemi "hiç boş yok" dese bile dosyanın devamı
/// bilinemez — NOT NULL kısıtı aktarımı yarıda kırardı.
/// </summary>
public static class DosyaTipiEslemesi
{
    public static string SqlTipi(string motorId, DosyaKolonu kolon)
    {
        int n = MetinBoyu(kolon.EnUzunMetin);
        return motorId switch
        {
            "mssql" => kolon.Tip switch
            {
                DosyaTipi.TamSayi => "bigint",
                DosyaTipi.Ondalik => "decimal(18,4)",
                DosyaTipi.Tarih => "datetime2",
                DosyaTipi.Bool => "bit",
                _ => n > 4000 ? "nvarchar(max)" : $"nvarchar({n})",
            },
            "postgres" => kolon.Tip switch
            {
                DosyaTipi.TamSayi => "bigint",
                DosyaTipi.Ondalik => "numeric(18,4)",
                DosyaTipi.Tarih => "timestamp",
                DosyaTipi.Bool => "boolean",
                _ => n > 4000 ? "text" : $"varchar({n})",
            },
            "mysql" => kolon.Tip switch
            {
                DosyaTipi.TamSayi => "bigint",
                DosyaTipi.Ondalik => "decimal(18,4)",
                DosyaTipi.Tarih => "datetime",
                DosyaTipi.Bool => "tinyint(1)",
                _ => n > 4000 ? "text" : $"varchar({n})",
            },
            "oracle" => kolon.Tip switch
            {
                DosyaTipi.TamSayi => "NUMBER(19)",
                DosyaTipi.Ondalik => "NUMBER(18,4)",
                DosyaTipi.Tarih => "DATE",
                DosyaTipi.Bool => "NUMBER(1)",
                _ => n > 2000 ? "NCLOB" : $"NVARCHAR2({n})", // NVARCHAR2 üst sınırı 2000'dir
            },
            _ => "nvarchar(4000)", // bilinmeyen motor — güvenli geniş metin
        };
    }

    private static int MetinBoyu(int enUzun) => enUzun switch
    {
        <= 0 => 100, // hep boş kolon — makul varsayılan
        <= 50 => 50,
        <= 100 => 100,
        <= 255 => 255,
        <= 1000 => 1000,
        <= 4000 => 4000,
        _ => enUzun,
    };
}
