namespace SjolystInnovation.AppKit.Email;

/// <summary>The company details shown in the header of <see cref="EmailLayout"/>. Blank details are left out.</summary>
/// <param name="Name">The brand name, shown large in the header.</param>
public sealed record EmailCompany(string Name)
{
    /// <summary>The legal company name, shown in the header.</summary>
    public string? LegalName { get; init; }

    /// <summary>
    /// The organization number, for example <c>923202374</c> or <c>923 202 374</c>. The header shows it in
    /// the email's language, as <c>Org.nr. 923 202 374</c> in Norwegian and <c>Org. no. 923 202 374</c> in
    /// English. Nine digits are grouped in threes, and anything else is shown trimmed as given. A blank
    /// number leaves the line out.
    /// </summary>
    public string? OrgNumber { get; init; }

    /// <summary>
    /// Whether the company is registered for VAT. Adds <c>MVA</c> after the organization number in every
    /// language, as Norwegian bookkeeping rules require for a registered business. Without a number it
    /// shows nothing.
    /// </summary>
    public bool VatRegistered { get; init; }

    /// <summary>
    /// The register the company is entered in, such as <c>Foretaksregisteret</c>. Shown after the
    /// organization number, and left out without a number.
    /// </summary>
    public string? RegisterName { get; init; }

    /// <summary>The postal address, shown in the header.</summary>
    public string? Address { get; init; }

    /// <summary>The contact email address, shown in the header.</summary>
    public string? Email { get; init; }
}

/// <summary>The language of the labels <see cref="EmailLayout"/> writes, such as the organization number label.</summary>
public enum EmailLayoutLanguage
{
    /// <summary>English.</summary>
    English,

    /// <summary>Norwegian Bokmål.</summary>
    Norwegian,
}
