using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SirThaddeus.UI.Avalonia;

public partial class MainWindow
{
    private async void AddProfileButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!EnsureProfilesRuntimeConnected())
        {
            return;
        }

        try
        {
            var template = await _runtimeApiClient!.GetProfileTemplateAsync(CreateSuggestedProfileId(), CancellationToken.None);
            var documentJson = await JsonDocumentEditorWindow.ShowAsync(
                this,
                "Add Memory Profile",
                "Edit the starter JSON and save it back to the runtime.",
                template.DocumentJson);
            if (documentJson is null)
            {
                return;
            }

            var result = await _runtimeApiClient.CreateProfileAsync(documentJson, CancellationToken.None);
            AppendTranscript($"[system] {result.Message}");
            await RefreshProfilesAsync();
            SelectProfile(result.ProfileId);
        }
        catch (Exception ex)
        {
            HandleProfileCrudFailure("Profile save failed", ex);
        }
    }

    private async void EditProfileButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!EnsureProfilesRuntimeConnected() || ProfilesList.SelectedItem is not ProfileListItemViewModel selected)
        {
            return;
        }

        try
        {
            var document = await _runtimeApiClient!.GetProfileDocumentAsync(selected.ProfileId, CancellationToken.None);
            var editedJson = await JsonDocumentEditorWindow.ShowAsync(
                this,
                $"Edit Memory Profile: {selected.DisplayName}",
                "Edit the JSON and keep profile_id unchanged to update this profile.",
                document.DocumentJson);
            if (editedJson is null)
            {
                return;
            }

            var result = await _runtimeApiClient.UpdateProfileAsync(selected.ProfileId, editedJson, CancellationToken.None);
            AppendTranscript($"[system] {result.Message}");
            await RefreshProfilesAsync();
            SelectProfile(result.ProfileId);
        }
        catch (Exception ex)
        {
            HandleProfileCrudFailure("Profile save failed", ex);
        }
    }

    private async void DeleteProfileButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!EnsureProfilesRuntimeConnected() || ProfilesList.SelectedItem is not ProfileListItemViewModel selected)
        {
            return;
        }

        var confirmed = await ConfirmationDialogWindow.ShowAsync(
            this,
            "Delete Memory Profile",
            $"Delete '{selected.DisplayName}'?\n\nThis removes the profile from the runtime.",
            "Delete");
        if (!confirmed)
        {
            return;
        }

        try
        {
            var result = await _runtimeApiClient!.DeleteProfileAsync(selected.ProfileId, CancellationToken.None);
            AppendTranscript($"[system] {result.Message}");
            await RefreshProfilesAsync();
            if (!string.IsNullOrWhiteSpace(result.ActiveProfileId))
            {
                SelectProfile(result.ActiveProfileId);
            }
        }
        catch (Exception ex)
        {
            HandleProfileCrudFailure("Profile delete failed", ex);
        }
    }

    private async void AddPersonalityButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!EnsureProfilesRuntimeConnected())
        {
            return;
        }

        try
        {
            var template = await _runtimeApiClient!.GetPersonalityTemplateAsync(CreateSuggestedPersonalityId(), CancellationToken.None);
            var documentJson = await JsonDocumentEditorWindow.ShowAsync(
                this,
                "Add Personality",
                "Edit the starter JSON and save it back to the runtime.",
                template.DocumentJson);
            if (documentJson is null)
            {
                return;
            }

            var result = await _runtimeApiClient.CreatePersonalityAsync(documentJson, CancellationToken.None);
            AppendTranscript($"[system] {result.Message}");
            await RefreshProfilesAsync();
            SelectPersonality(result.PersonalityId);
        }
        catch (Exception ex)
        {
            HandleProfileCrudFailure("Personality save failed", ex);
        }
    }

    private async void EditPersonalityButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!EnsureProfilesRuntimeConnected() || PersonalitiesList.SelectedItem is not PersonalityListItemViewModel selected)
        {
            return;
        }

        try
        {
            var document = await _runtimeApiClient!.GetPersonalityDocumentAsync(selected.Id, CancellationToken.None);
            var editedJson = await JsonDocumentEditorWindow.ShowAsync(
                this,
                $"Edit Personality: {selected.DisplayName}",
                "Edit the JSON and keep id unchanged to update this personality.",
                document.DocumentJson);
            if (editedJson is null)
            {
                return;
            }

            var result = await _runtimeApiClient.UpdatePersonalityAsync(selected.Id, editedJson, CancellationToken.None);
            AppendTranscript($"[system] {result.Message}");
            await RefreshProfilesAsync();
            SelectPersonality(result.PersonalityId);
        }
        catch (Exception ex)
        {
            HandleProfileCrudFailure("Personality save failed", ex);
        }
    }

    private async void DeletePersonalityButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!EnsureProfilesRuntimeConnected() || PersonalitiesList.SelectedItem is not PersonalityListItemViewModel selected)
        {
            return;
        }

        var confirmed = await ConfirmationDialogWindow.ShowAsync(
            this,
            "Delete Personality",
            $"Delete '{selected.DisplayName}'?\n\nThis removes the personality JSON file from the runtime profile directory.",
            "Delete");
        if (!confirmed)
        {
            return;
        }

        try
        {
            var result = await _runtimeApiClient!.DeletePersonalityAsync(selected.Id, CancellationToken.None);
            AppendTranscript($"[system] {result.Message}");
            await RefreshProfilesAsync();
            SelectPersonality(result.ActivePersonalityId);
        }
        catch (Exception ex)
        {
            HandleProfileCrudFailure("Personality delete failed", ex);
        }
    }

    private bool EnsureProfilesRuntimeConnected()
    {
        if (_runtimeApiClient is not null)
        {
            return true;
        }

        ProfilesStatusText.Text = "Profiles: runtime not connected";
        return false;
    }

    private void HandleProfileCrudFailure(string operation, Exception ex)
    {
        var message = $"{operation}: {ex.Message}";
        ProfilesStatusText.Text = message;
        AppendTranscript("[error] " + message);
    }

    private void SelectProfile(string? profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return;
        }

        ProfilesList.SelectedItem = _profileItems.FirstOrDefault(item =>
            string.Equals(item.ProfileId, profileId, StringComparison.OrdinalIgnoreCase));
    }

    private void SelectPersonality(string? personalityId)
    {
        if (string.IsNullOrWhiteSpace(personalityId))
        {
            return;
        }

        PersonalitiesList.SelectedItem = _personalityItems.FirstOrDefault(item =>
            string.Equals(item.Id, personalityId, StringComparison.OrdinalIgnoreCase));
    }

    private static string CreateSuggestedProfileId()
        => ($"profile-{Guid.NewGuid():N}")[..16];

    private static string CreateSuggestedPersonalityId()
        => ($"personality_{Guid.NewGuid():N}")[..20];
}
