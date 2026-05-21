using System;

namespace WinAgent.Models;

public class IpcMessage
{
    public string? Id { get; set; }        // Unique message ID for request-response pairing (optional, but highly recommended for 2-way RPC)
    public string? Type { get; set; }      // "Request", "Response", "Auth", "AuthResponse", or "Event"
    public string? Token { get; set; }     // For authentication (first message must include this)
    public string? Path { get; set; }      // E.g., "system/notify" or "tray/notify-click"
    public string? Payload { get; set; }   // JSON serialized payload or request/response data
    public bool? Success { get; set; }     // True/false for responses
    public string? Error { get; set; }     // Error message if any
}
