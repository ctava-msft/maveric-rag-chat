metadata description = 'Create role assignment and definition resources.'

@description('Id of the service principals to assign database and application roles.')
param appPrincipalId string = ''

@description('Id of the user principals to assign database and application roles.')
param userPrincipalId string = ''

@description('Principal type used for the role assignment.')
param principalType string = ''

module openaiAppAssignment './role/assignment.bicep' = if (!empty(appPrincipalId)) {
  name: 'openai-role-assignment-read-app'
  params: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'
    ) // Cognitive Services OpenAI User built-in role
    principalId: appPrincipalId // Principal to assign role
    principalType: 'ServicePrincipal' // Specify the principal type // was 'None' but this appears to cause issues
  }
}

module openaiUserAssignment './role/assignment.bicep' = if (!empty(userPrincipalId)) {
  name: 'openai-role-assignment-read-user'
  params: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'
    ) // Cognitive Services OpenAI User built-in role
    principalId: userPrincipalId // Principal to assign role
    principalType: !empty(principalType) ? principalType : 'User' // Principal type or current deployment user
  }
}
