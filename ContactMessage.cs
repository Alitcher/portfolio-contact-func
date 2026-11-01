namespace portfolio_contact_func;

// One contact message, as it travels through the queue and into SQL.
public record ContactMessage(Guid Id, string Name, string Email, string Message, DateTime CreatedAt);
// The shape of one message. The same object goes on the queue and later into the table.