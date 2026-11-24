using Cus.Core.Crypto;

namespace Cus.Core.Tests;

/// <summary>
/// CPace-ristretto255-SHA512: test vector di draft-irtf-cfrg-cpace-21 (appendici A e B.3)
/// e prove di andata e ritorno.
/// </summary>
public class CPaceTests
{
    // --- Appendice B.3 ---
    private static readonly byte[] Prs = "Password"u8.ToArray();
    private static readonly byte[] Ci = Hex("0b415f696e69746961746f720b425f726573706f6e646572");
    private static readonly byte[] Sid = Hex("7e4b4791d6a8ef019b936c79fb7f2c57");
    private static readonly byte[] AdA = "ADa"u8.ToArray();
    private static readonly byte[] AdB = "ADb"u8.ToArray();

    private static readonly byte[] Ya = Hex("d6bac480f2c386c394efc7c47adb9925dcd2630b64f240c50f8d0eec482b9157");
    private static readonly byte[] Yb = Hex("3ea7e0b19560d7c0b0f5734f63b955286dfa8232b5ebe63324e2d9e7433f7258");
    private static readonly byte[] ScalarA = Hex("da3d23700a9e5699258aef94dc060dfda5ebb61f02a5ea77fad53f4ff0976d08");
    private static readonly byte[] ScalarB = Hex("d2316b454718c35362d83d69df6320f38578ed5984651435e2949762d900b80d");
    private static readonly byte[] K = Hex("80b69a8a76457ab6a4d7f887a4bf6b55a2f80ac19c333f917a05fc9887c8b40f");

    private static readonly byte[] IskIr = Hex(
        "b69effbf61b51d56401c0f65601abe428de8206feaaf0e32198896dcae7b35cd" +
        "2b38950a39dfd5d4a79164614c2984f7daa460b588c1e80c3fa2068af7900447");

    private static readonly byte[] TranscriptIrVector = Hex(
        "20d6bac480f2c386c394efc7c47adb9925dcd2630b64f240c50f8d0eec482b91" +
        "5703414461203ea7e0b19560d7c0b0f5734f63b955286dfa8232b5ebe63324e2" +
        "d9e7433f725803414462");

    private static readonly byte[] TranscriptOcVector = Hex(
        "6f6320d6bac480f2c386c394efc7c47adb9925dcd2630b64f240c50f8d0eec48" +
        "2b915703414461203ea7e0b19560d7c0b0f5734f63b955286dfa8232b5ebe633" +
        "24e2d9e7433f725803414462");

    // Scalare e punti di B.3.10 / B.3.11.
    private static readonly byte[] S = Hex("7cd0e075fa7955ba52c02759a6c90dbbfc10e6d40aea8d283e407d88cf538a05");
    private static readonly byte[] X = Hex("2c3c6b8c4f3800e7aef6864025b4ed79bd599117e427c41bd47d93d654b4a51c");
    private static readonly byte[] YInvalid1 = Hex("2b3c6b8c4f3800e7aef6864025b4ed79bd599117e427c41bd47d93d654b4a51c");
    private static readonly byte[] Identity = new byte[32];

    // --- A.1 prepend_len / lv_cat ---

    [Fact]
    public void PrependLen_MatchesA12()
    {
        Assert.Equal(Hex("00"), CPace.PrependLen([]));
        Assert.Equal(Hex("0431323334"), CPace.PrependLen("1234"u8));

        var range127 = Enumerable.Range(0, 127).Select(i => (byte)i).ToArray();
        Assert.Equal(new byte[] { 0x7F }.Concat(range127).ToArray(), CPace.PrependLen(range127));

        var range128 = Enumerable.Range(0, 128).Select(i => (byte)i).ToArray();
        Assert.Equal(new byte[] { 0x80, 0x01 }.Concat(range128).ToArray(), CPace.PrependLen(range128));
    }

    [Fact]
    public void LvCat_MatchesA14()
    {
        var result = CPace.LvCat("1234"u8.ToArray(), "5"u8.ToArray(), [], "678"u8.ToArray());
        Assert.Equal(Hex("043132333401350003363738"), result);
    }

    // --- A.3 o_cat e transcript ---

    [Theory]
    [InlineData("00", "0000", false)]
    [InlineData("01", "0000", true)]
    [InlineData("0000", "00", true)]
    [InlineData("0000", "01", false)]
    [InlineData("0001", "01", false)]
    [InlineData("41424344", "424344", false)]
    public void LexicographicallyLarger_MatchesA33(string bytes1, string bytes2, bool expected)
    {
        Assert.Equal(expected, CPace.LexicographicallyLarger(Hex(bytes1), Hex(bytes2)));
    }

    [Fact]
    public void OCat_MatchesA33()
    {
        Assert.Equal(Hex("6f6342434441424344"), CPace.OCat("ABCD"u8, "BCD"u8));
        Assert.Equal(Hex("6f634243444142434445"), CPace.OCat("BCD"u8, "ABCDE"u8));
    }

    [Fact]
    public void TranscriptIr_MatchesA35()
    {
        Assert.Equal(
            Hex("03313233065061727479410332333406506172747942"),
            CPace.TranscriptIr("123"u8, "PartyA"u8, "234"u8, "PartyB"u8));
        Assert.Equal(
            Hex("043334353606506172747941043233343506506172747942"),
            CPace.TranscriptIr("3456"u8, "PartyA"u8, "2345"u8, "PartyB"u8));
    }

    [Fact]
    public void TranscriptOc_MatchesA37()
    {
        Assert.Equal(
            Hex("6f6303323334065061727479420331323306506172747941"),
            CPace.TranscriptOc("123"u8, "PartyA"u8, "234"u8, "PartyB"u8));
        Assert.Equal(
            Hex("6f63043334353606506172747941043233343506506172747942"),
            CPace.TranscriptOc("3456"u8, "PartyA"u8, "2345"u8, "PartyB"u8));
    }

    // --- B.3.1 generatore ---

    [Fact]
    public void GeneratorString_MatchesB31()
    {
        var expected = Hex(
            "11435061636552697374726574746f3235350850617373776f726464" +
            new string('0', 200) +
            "180b415f696e69746961746f720b425f726573706f6e646572107e4b4791d6a8ef019b936c79fb7f2c57");

        var genStr = CPace.GeneratorString(CPace.Dsi, Prs, Ci, Sid, CPace.HashBlockBytes);

        Assert.Equal(170, genStr.Length);
        Assert.Equal(expected, genStr);
        Assert.Equal(
            Hex("da6d3ddc8802fca9058755ffd3ebde08a9c2c74945901a258482a288b6663af0" +
                "6bf645c93cd1c51512307199c80e84908916d983b34af77205f90851a657ee27"),
            System.Security.Cryptography.SHA512.HashData(genStr));
    }

    [Fact]
    public void CalculateGenerator_MatchesB31()
    {
        Assert.Equal(
            Hex("222b6b195fe84b1652badb6f6a3ae3d24341e7306967f0b8115b40d5698c7e56"),
            CPace.CalculateGenerator(Prs, Ci, Sid));
    }

    // --- B.3.2 - B.3.5 messaggi, K e ISK ---

    [Fact]
    public void Messages_MatchB32AndB33()
    {
        var a = CPace.CreateWithScalar(CPaceRole.Initiator, Prs, Ci, Sid, AdA, ScalarA);
        var b = CPace.CreateWithScalar(CPaceRole.Responder, Prs, Ci, Sid, AdB, ScalarB);

        Assert.Equal(Ya, a.Message);
        Assert.Equal(Yb, b.Message);
    }

    [Fact]
    public void SecretPoint_MatchesB34()
    {
        Assert.Equal(K, CPace.ScalarMultVfy(ScalarA, Yb));
        Assert.Equal(K, CPace.ScalarMultVfy(ScalarB, Ya));
    }

    [Fact]
    public void IskInitiatorResponder_MatchesB35()
    {
        Assert.Equal(TranscriptIrVector, CPace.TranscriptIr(Ya, AdA, Yb, AdB));
        Assert.Equal(Hex("435061636552697374726574746f3235355f49534b"), CPace.DsiIsk.ToArray());
        Assert.Equal(
            Hex("15435061636552697374726574746f3235355f49534b107e4b4791d6a8ef019b936c79fb7f2c57" +
                "2080b69a8a76457ab6a4d7f887a4bf6b55a2f80ac19c333f917a05fc9887c8b40f"),
            CPace.LvCat(CPace.DsiIsk.ToArray(), Sid, K));
        Assert.Equal(IskIr, CPace.ComputeIsk(Sid, K, TranscriptIrVector));

        var a = CPace.CreateWithScalar(CPaceRole.Initiator, Prs, Ci, Sid, AdA, ScalarA);
        var b = CPace.CreateWithScalar(CPaceRole.Responder, Prs, Ci, Sid, AdB, ScalarB);

        Assert.Equal(IskIr, a.DeriveIsk(b.Message, AdB));
        Assert.Equal(IskIr, b.DeriveIsk(a.Message, AdA));
    }

    // --- B.3.6 - B.3.7 modalità parallela e session id opzionale ---

    [Fact]
    public void IskParallel_MatchesB36()
    {
        Assert.Equal(TranscriptOcVector, CPace.TranscriptOc(Ya, AdA, Yb, AdB));
        Assert.Equal(
            Hex("544199d71f62f8d9a1fee55727e24fe4a45844593c2b6013c4fa3969d0e5debb" +
                "2244675c0b43397cbb68d342b01fc0f98fc961469a25134de9f0f813c1a57476"),
            CPace.ComputeIsk(Sid, K, TranscriptOcVector));
    }

    [Fact]
    public void SidOutput_MatchesB37()
    {
        Assert.Equal(
            Hex("bb1c449b35f0ea79a65c209f329a693d475e0ce2387bed9fe4b78f60b2a27c21" +
                "9813fb2cfe175ef40d2222d9261e66da7d78f7c55a303b1b8611dcdfab880c47"),
            CPace.SidOutput(TranscriptIrVector));
        Assert.Equal(
            Hex("10d5941d4933497fe31b9188d690b84465e2a2d158332a7267284a071a8d0876" +
                "fc5c8c329dc735d59a9f8ef6623ee23924704a2f929dd631ca981227ee82fff2"),
            CPace.SidOutput(TranscriptOcVector));
    }

    // --- B.3.10 - B.3.11 scalar_mult / scalar_mult_vfy ---

    [Fact]
    public void ScalarMult_ValidInputs_MatchesB310()
    {
        var expected = Hex("7c13645fe790a468f62c39beb7388e541d8405d1ade69d1778c5fe3e7f6b600e");

        Assert.Equal(expected, CPace.ScalarMult(S, X));
        Assert.Equal(expected, CPace.ScalarMultVfy(S, X));
    }

    [Fact]
    public void ScalarMultVfy_InvalidInputs_ReturnIdentity_B311()
    {
        Assert.Equal(Identity, CPace.ScalarMultVfy(S, YInvalid1));
        Assert.Equal(Identity, CPace.ScalarMultVfy(S, Identity));
    }

    [Fact]
    public void DeriveIsk_RejectsInvalidPointsFromB311()
    {
        var a = CPace.CreateWithScalar(CPaceRole.Initiator, Prs, Ci, Sid, AdA, ScalarA);
        Assert.Throws<CPaceException>(() => a.DeriveIsk(YInvalid1, AdB));

        var b = CPace.CreateWithScalar(CPaceRole.Responder, Prs, Ci, Sid, AdB, ScalarB);
        Assert.Throws<CPaceException>(() => b.DeriveIsk(Identity, AdA));
    }

    // --- Andata e ritorno ---

    [Fact]
    public void RoundTrip_SamePassword_SameIsk()
    {
        var (iskA, iskB) = Run("123456"u8.ToArray(), "123456"u8.ToArray());

        Assert.Equal(CPace.IskBytes, iskA.Length);
        Assert.Equal(iskA, iskB);
    }

    [Fact]
    public void RoundTrip_DifferentPasswords_DifferentIsk()
    {
        var (iskA, iskB) = Run("123456"u8.ToArray(), "123457"u8.ToArray());

        Assert.NotEqual(iskA, iskB);
    }

    [Fact]
    public void RoundTrip_FreshScalarsEachRun()
    {
        var first = CPace.Create(CPaceRole.Initiator, "123456"u8, Ci, Sid, AdA);
        var second = CPace.Create(CPaceRole.Initiator, "123456"u8, Ci, Sid, AdA);

        Assert.Equal(CPace.MessageBytes, first.Message.Length);
        Assert.NotEqual(first.Message, second.Message);
    }

    [Fact]
    public void DeriveIsk_RejectsIdentityAndGarbage()
    {
        Assert.Throws<CPaceException>(() => NewParty().DeriveIsk(Identity, AdB));
        Assert.Throws<CPaceException>(() => NewParty().DeriveIsk(Enumerable.Repeat((byte)0xFF, 32).ToArray(), AdB));
        Assert.Throws<CPaceException>(() => NewParty().DeriveIsk(new byte[31], AdB));
        Assert.Throws<CPaceException>(() => NewParty().DeriveIsk(new byte[33], AdB));
        Assert.Throws<CPaceException>(() => NewParty().DeriveIsk([], AdB));

        static CPace NewParty() => CPace.Create(CPaceRole.Initiator, "123456"u8, Ci, Sid, AdA);
    }

    [Fact]
    public void DeriveIsk_IsSingleUse()
    {
        var a = CPace.Create(CPaceRole.Initiator, "123456"u8, Ci, Sid, AdA);
        var b = CPace.Create(CPaceRole.Responder, "123456"u8, Ci, Sid, AdB);

        a.DeriveIsk(b.Message, AdB);

        Assert.Throws<InvalidOperationException>(() => a.DeriveIsk(b.Message, AdB));
    }

    private static (byte[] IskA, byte[] IskB) Run(byte[] prsA, byte[] prsB)
    {
        var a = CPace.Create(CPaceRole.Initiator, prsA, Ci, Sid, AdA);
        var b = CPace.Create(CPaceRole.Responder, prsB, Ci, Sid, AdB);
        return (a.DeriveIsk(b.Message, AdB), b.DeriveIsk(a.Message, AdA));
    }

    private static byte[] Hex(string hex) => Convert.FromHexString(hex);
}
