﻿using PartialSourceGen;
using System.ComponentModel.DataAnnotations;

namespace Sample.Models;

/// <summary>
/// A person
/// </summary>
[Partial(IncludeRequiredProperties = false, Summary = "A partial person")]
public readonly record struct Person
{
    public Person()
    {
    }

    [Required]
    public required int ID { get; init; }

    /// <summary>
    /// The first name
    /// </summary>
    public string FirstName { get; init; } = string.Empty;

    /// <summary>
    /// The last name
    /// </summary>
    public string? LastName { get; init; }
}