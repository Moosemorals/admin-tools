Feature: Email Routing
  As a registered user with a valid S/MIME certificate
  I want to send emails to route requests to Copilot
  So that I can interact with Copilot via email

  Background:
    Given the email processor is configured with a valid project "TestProject"

  Scenario: Valid S/MIME email with known project is handed to Copilot
    Given a registered certificate for "user@example.com"
    When I send a signed email from "user@example.com" with subject "TestProject" and body "Hello Copilot"
    Then the email.messages.processed counter is incremented with outcome "success"
    And the email.messages.received counter is incremented

  Scenario: Valid S/MIME email with unknown project triggers reply
    Given a registered certificate for "user@example.com"
    When I send a signed email from "user@example.com" with subject "UnknownProject" and body "Hello"
    Then the email.messages.replied counter is incremented with outcome "unknown_project"
    And no email.messages.processed event is recorded

  Scenario: Unsigned email is silently dropped
    When I send an unsigned email from "user@example.com" with subject "TestProject" and body "Hello"
    Then the email.messages.dropped counter is incremented with outcome "unsigned"
    And no reply is sent

  Scenario: Email signed with expired certificate is silently dropped
    Given an expired certificate for "user@example.com"
    When I send a signed email from "user@example.com" with subject "TestProject" and body "Hello"
    Then the email.messages.dropped counter is incremented with outcome "expired_certificate"
    And no reply is sent

  Scenario: Email signed with revoked certificate is silently dropped
    Given a revoked certificate for "user@example.com"
    When I send a signed email from "user@example.com" with subject "TestProject" and body "Hello"
    Then the email.messages.dropped counter is incremented with outcome "revoked_certificate"
    And no reply is sent

  Scenario: Email signed with unknown certificate is silently dropped
    When I send a signed email from "user@example.com" with an unregistered certificate with subject "TestProject" and body "Hello"
    Then the email.messages.dropped counter is incremented with outcome "unknown_signature"
    And no reply is sent

  Scenario: Email with tampered body (invalid signature) is silently dropped
    Given a registered certificate for "user@example.com"
    When I send a signed email from "user@example.com" with a tampered body with subject "TestProject"
    Then the email.messages.dropped counter is incremented with outcome "invalid_signature"
    And no reply is sent
