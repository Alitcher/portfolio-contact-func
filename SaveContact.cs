using System.Data;
using System.Net;
using System.Net.Mail;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace portfolio_contact_func;

// Runs for each message on the queue, saves it to Azure SQL, then emails a copy.
public class SaveContact(ILogger<SaveContact> logger)
{
    // SQL Server error number for "a row with this primary key already exists".
    private const int DuplicateKey = 2627;

    [Function("SaveContact")]
    public async Task Run(
        [QueueTrigger("contact-messages", Connection = "ContactStorage")] ContactMessage contact)
    {
        var connectionString = Environment.GetEnvironmentVariable("SqlConnectionString")
            ?? throw new InvalidOperationException("SqlConnectionString is not set.");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            """
            INSERT INTO dbo.ContactMessages (Id, Name, Email, Message, CreatedAt)
            VALUES (@id, @name, @email, @message, @createdAt)
            """,
            connection);
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = contact.Id;
        command.Parameters.Add("@name", SqlDbType.NVarChar, 100).Value = contact.Name;
        command.Parameters.Add("@email", SqlDbType.NVarChar, 254).Value = contact.Email;
        command.Parameters.Add("@message", SqlDbType.NVarChar, 4000).Value = contact.Message;
        command.Parameters.Add("@createdAt", SqlDbType.DateTime2).Value = contact.CreatedAt;

        try
        {
            await command.ExecuteNonQueryAsync();
            logger.LogInformation("Saved contact message {Id}", contact.Id);
        }
        catch (SqlException ex) when (ex.Number == DuplicateKey)
        {
            // Queues can deliver the same message twice. It's already saved, so this is fine.
            logger.LogInformation("Contact message {Id} was already saved", contact.Id);
        }

        // If sending fails, this throws and the queue retries. The retry hits the
        // duplicate key above (already saved) and then tries the email again.
        await SendEmailAsync(contact);
    }

    // Sends the message to the site owner through Gmail SMTP.
    // Settings: GmailAddress, GmailAppPassword, and optional NotifyEmail (defaults to GmailAddress).
    private async Task SendEmailAsync(ContactMessage contact)
    {
        var gmail = Environment.GetEnvironmentVariable("GmailAddress");
        // Google shows app passwords with spaces ("abcd efgh ..."); drop them.
        var appPassword = Environment.GetEnvironmentVariable("GmailAppPassword")?.Replace(" ", "");
        if (string.IsNullOrWhiteSpace(gmail) || string.IsNullOrWhiteSpace(appPassword))
        {
            logger.LogWarning("Gmail settings are missing, so no email was sent for {Id}", contact.Id);
            return;
        }
        var notifyTo = Environment.GetEnvironmentVariable("NotifyEmail");
        if (string.IsNullOrWhiteSpace(notifyTo)) notifyTo = gmail;

        // Line breaks are not allowed in a subject line.
        var safeName = contact.Name.Replace("\r", " ").Replace("\n", " ");

        using var mail = new MailMessage(gmail, notifyTo)
        {
            Subject = $"Portfolio contact from {safeName}",
            Body = $"From: {contact.Name} <{contact.Email}>\nSent: {contact.CreatedAt:yyyy-MM-dd HH:mm} UTC\n\n{contact.Message}",
        };
        // So "Reply" in the inbox goes to the visitor.
        mail.ReplyToList.Add(new MailAddress(contact.Email, safeName));

        using var smtp = new SmtpClient("smtp.gmail.com", 587)
        {
            EnableSsl = true,
            Credentials = new NetworkCredential(gmail, appPassword),
        };
        await smtp.SendMailAsync(mail);
        logger.LogInformation("Emailed contact message {Id}", contact.Id);
    }
}
