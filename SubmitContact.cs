using System.Net.Mail;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Extensions.Http.AspNetCore;
using Microsoft.Extensions.Logging;

namespace portfolio_contact_func;

// POST /api/contact - checks the form and puts it on the queue. Saving happens in SaveContact.
public class SubmitContact(ILogger<SubmitContact> logger)
{
    [Function("SubmitContact")]
    public async Task<SubmitContactOutput> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "contact")] HttpRequest req)
    {
        ContactForm? form;
        try
        {
            form = await req.ReadFromJsonAsync<ContactForm>();
        }
        catch (JsonException)
        {
            form = null;
        }
        if (form is null) return Bad("Invalid request body.");

        // Honeypot: the field is hidden from people, so only bots fill it in.
        // Answer "ok" so the bot doesn't learn it was caught, but queue nothing.
        if (!string.IsNullOrEmpty(form.Website))
            return new SubmitContactOutput { Result = new AcceptedResult() };

        var name = form.Name?.Trim() ?? "";
        var email = form.Email?.Trim() ?? "";
        var message = form.Message?.Trim() ?? "";

        if (name.Length is 0 or > 100) return Bad("Name is required (max 100 characters).");
        if (email.Length > 254 || !MailAddress.TryCreate(email, out _)) return Bad("A valid email is required.");
        if (message.Length is 0 or > 4000) return Bad("Message is required (max 4000 characters).");

        // The Id is made here, before queueing, so a message delivered twice is saved only once.
        var contact = new ContactMessage(Guid.NewGuid(), name, email, message, DateTime.UtcNow);
        logger.LogInformation("Queued contact message {Id}", contact.Id);

        return new SubmitContactOutput { Message = contact, Result = new AcceptedResult() };
    }

    private static SubmitContactOutput Bad(string error) =>
        new() { Result = new BadRequestObjectResult(new { error }) };
}

// What the browser sends. Website is the hidden honeypot field.
public record ContactForm(string? Name, string? Email, string? Message, string? Website);

// Two outputs from one function: the HTTP answer, and (if valid) a queue message.
public class SubmitContactOutput
{
    [QueueOutput("contact-messages", Connection = "ContactStorage")]
    public ContactMessage? Message { get; set; }

    [HttpResult]
    public IActionResult Result { get; set; } = new AcceptedResult();
}
