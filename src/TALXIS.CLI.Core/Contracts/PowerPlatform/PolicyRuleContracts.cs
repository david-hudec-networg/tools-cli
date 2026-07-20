using TALXIS.CLI.Core.Model;

namespace TALXIS.CLI.Core.Contracts.PowerPlatform;

/// <summary>
/// A role assignment scoped to a single environment group (as opposed to the
/// whole tenant — see <see cref="PowerPlatformTenantRoleAssignment"/>).
/// Same wire shape as tenant/environment role assignments
/// (<c>principalObjectId</c>/<c>principalType</c>/<c>roleDefinitionId</c>/
/// <c>scope</c>), scoped to
/// <c>/environmentGroups/{environmentGroupId}</c> instead of
/// <c>/tenants/{tenantId}</c>.
/// </summary>
public sealed record PowerPlatformEnvironmentGroupRoleAssignment(
    string RoleAssignmentId,
    Guid EnvironmentGroupId,
    PowerPlatformPrincipalType PrincipalType,
    Guid PrincipalObjectId,
    Guid RoleDefinitionId,
    DateTimeOffset? CreatedOn,
    DateTimeOffset? ExpiresOn);

/// <summary>
/// Client abstraction over environment-group role-assignment endpoints under
/// <c>api.powerplatform.com/authorization/environmentGroups/{id}/roleAssignments</c>
/// (Preview).
/// </summary>
public interface IPowerPlatformEnvironmentGroupRoleClient
{
    Task<IReadOnlyList<PowerPlatformEnvironmentGroupRoleAssignment>> ListAsync(
        Connection connection, Credential credential, Guid environmentGroupId, CancellationToken ct);

    Task<PowerPlatformEnvironmentGroupRoleAssignment> AddAsync(
        Connection connection, Credential credential, Guid environmentGroupId,
        PowerPlatformPrincipalType principalType, Guid principalObjectId, Guid roleDefinitionId, CancellationToken ct);

    Task RemoveAsync(
        Connection connection, Credential credential, Guid environmentGroupId, string roleAssignmentId, CancellationToken ct);
}

/// <summary>
/// The resource an <see cref="PowerPlatformPolicyRuleAssignment"/> targets.
/// </summary>
public enum PowerPlatformPolicyAssignmentResourceType
{
    EnvironmentGroup = 0,
    Environment = 1,
}

/// <summary>
/// How a rule behaves when applied to its target resource (e.g. enforced
/// vs. audit-only). Kept as a plain string wrapper (rather than a closed
/// enum) because the exact set of behavior types for "Advanced Connector
/// Policy" rules was not confirmed against a live API response as of this
/// writing — see <c>envgroup-policy-api-investigation</c>. Do not add new
/// named members here until that investigation confirms the real value set;
/// pass whatever string value the target API accepts.
/// </summary>
public sealed record PowerPlatformPolicyBehaviorType(string Value)
{
    public override string ToString() => Value;
}

/// <summary>
/// A single rule-based policy — the modern governance/policy framework that
/// is replacing classic DLP policies. Represents one policy definition
/// (e.g. an "Advanced connector policy" rule) as returned by
/// <c>api.powerplatform.com/governance/ruleBasedPolicies</c>.
/// </summary>
/// <remarks>
/// <see cref="RuleDefinition"/> is intentionally an untyped JSON payload
/// (<see cref="System.Text.Json.JsonElement"/>-serializable string) rather
/// than a strongly-typed DTO: the exact schema for "Advanced Connector
/// Policy" rule content was not found in any research source consulted
/// while planning this feature (Microsoft Learn REST reference, the
/// decompiled <c>pac</c> CLI, and the public Terraform provider all target
/// slightly different API generations). Strongly typing this prematurely
/// risks silently dropping or misinterpreting fields once real payloads are
/// captured. Replace this with a proper DTO once
/// <c>envgroup-policy-api-investigation</c> confirms the real shape.
/// </remarks>
public sealed record PowerPlatformPolicyRule(
    Guid Id,
    string DisplayName,
    string? Description,
    string RuleType,
    string RuleDefinitionJson,
    DateTimeOffset? CreatedOn,
    DateTimeOffset? LastModifiedOn);

/// <summary>
/// Fields accepted when creating or updating a rule-based policy. See
/// <see cref="PowerPlatformPolicyRule.RuleDefinitionJson"/> remarks on why
/// the rule payload itself is untyped JSON at this stage.
/// </summary>
public sealed record PowerPlatformPolicyRuleUpsertOptions(
    string DisplayName,
    string? Description,
    string RuleType,
    string RuleDefinitionJson);

/// <summary>
/// Records that a policy rule has been assigned to an environment group or a
/// single environment, along with the per-resource behavior override (if
/// any) requested at assignment time.
/// </summary>
public sealed record PowerPlatformPolicyRuleAssignment(
    Guid PolicyRuleId,
    PowerPlatformPolicyAssignmentResourceType ResourceType,
    Guid ResourceId,
    PowerPlatformPolicyBehaviorType? BehaviorType);

/// <summary>
/// Client abstraction over the rule-based-policy CRUD and assignment
/// endpoints under <c>api.powerplatform.com/governance/ruleBasedPolicies</c>.
/// </summary>
public interface IPowerPlatformPolicyRuleClient
{
    Task<IReadOnlyList<PowerPlatformPolicyRule>> ListAsync(
        Connection connection, Credential credential, CancellationToken ct);

    Task<PowerPlatformPolicyRule?> GetAsync(
        Connection connection, Credential credential, Guid policyRuleId, CancellationToken ct);

    Task<PowerPlatformPolicyRule> CreateAsync(
        Connection connection, Credential credential, PowerPlatformPolicyRuleUpsertOptions options, CancellationToken ct);

    Task<PowerPlatformPolicyRule> UpdateAsync(
        Connection connection, Credential credential, Guid policyRuleId, PowerPlatformPolicyRuleUpsertOptions options, CancellationToken ct);

    Task DeleteAsync(
        Connection connection, Credential credential, Guid policyRuleId, CancellationToken ct);

    Task<IReadOnlyList<PowerPlatformPolicyRuleAssignment>> ListAssignmentsAsync(
        Connection connection, Credential credential, PowerPlatformPolicyAssignmentResourceType resourceType, Guid resourceId, CancellationToken ct);

    Task AssignAsync(
        Connection connection, Credential credential, Guid policyRuleId,
        PowerPlatformPolicyAssignmentResourceType resourceType, Guid resourceId,
        PowerPlatformPolicyBehaviorType? behaviorType, CancellationToken ct);

    Task UnassignAsync(
        Connection connection, Credential credential, Guid policyRuleId,
        PowerPlatformPolicyAssignmentResourceType resourceType, Guid resourceId, CancellationToken ct);
}
