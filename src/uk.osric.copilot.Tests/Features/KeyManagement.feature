Feature: Key Management
  As an administrator
  I want to manage S/MIME certificates
  So that users can sign emails to authenticate with the system

  Scenario: Generate a new certificate for an email address
    When I generate a certificate for "admin@example.com" with validity 365 days
    Then a certificate is returned with a valid serial number
    And the certificate is not revoked
    And the certificate expires in approximately 365 days

  Scenario: List certificates for an email address
    Given 2 certificates exist for "admin@example.com"
    When I list certificates for "admin@example.com"
    Then 2 certificates are returned

  Scenario: Download a certificate as PFX
    Given a certificate exists for "admin@example.com"
    When I download the certificate
    Then I receive a non-empty PFX file

  Scenario: Revoke a certificate
    Given a certificate exists for "admin@example.com"
    When I revoke the certificate
    Then the certificate is marked as revoked
    And active certificates for "admin@example.com" does not include the revoked certificate

  Scenario: Revoked certificate is excluded from active list
    Given a revoked certificate exists for "admin@example.com"
    And a valid certificate exists for "admin@example.com"
    When I list active certificates for "admin@example.com"
    Then only 1 certificate is returned
