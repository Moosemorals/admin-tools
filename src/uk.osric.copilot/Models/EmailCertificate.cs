namespace uk.osric.copilot.Models {
    public class EmailCertificate {
        public int Id { get; set; }
        public string EmailAddress { get; set; } = null!;
        public string SubjectDn { get; set; } = null!;
        public string Fingerprint { get; set; } = null!;
        public byte[] PfxData { get; set; } = null!;
        public byte[] CertificateDer { get; set; } = null!;
        public DateTimeOffset NotBefore { get; set; }
        public DateTimeOffset NotAfter { get; set; }
        public bool IsRevoked { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }
}
