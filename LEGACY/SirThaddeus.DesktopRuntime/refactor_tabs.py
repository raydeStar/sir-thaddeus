import re
import os

xaml_path = r'c:\Users\Ayric\Source\Repos\sir-thaddeus\apps\desktop-runtime\SirThaddeus.DesktopRuntime\MainWindow.xaml'
with open(xaml_path, 'r', encoding='utf-8') as f:
    xaml = f.read()

# Make a backup
with open(xaml_path + '.bak', 'w', encoding='utf-8') as f:
    f.write(xaml)

# 1. Hide the top-level tab buttons
xaml = re.sub(
    r'<ToggleButton x:Name="MemoryTabButton".*?Style="\{StaticResource ViewTabStyle\}"\/>\s*', 
    '', xaml, flags=re.DOTALL)
xaml = re.sub(
    r'<ToggleButton x:Name="ProfileTabButton".*?Style="\{StaticResource ViewTabStyle\}"\/>\s*', 
    '', xaml, flags=re.DOTALL)
xaml = re.sub(
    r'<ToggleButton x:Name="LogsTabButton".*?Style="\{StaticResource ViewTabStyle\}"\/>\s*', 
    '', xaml, flags=re.DOTALL)

# 2. Modify SettingsView to include sub-tabs and moving the content into a container
# Find the start of SettingsView
settings_start_idx = xaml.find('<Grid Grid.Row="1" x:Name="SettingsView" Visibility="Collapsed">')
if settings_start_idx == -1:
    print("Could not find SettingsView")
    exit(1)

# Find the end of SettingsView grid (it's around line 1900, but we can just use regex or find matching tag)
# Actually, if we just replace the inner contents, we can do it safely.
# Replace the top of SettingsView:
old_settings_top = """        <Grid Grid.Row="1" x:Name="SettingsView" Visibility="Collapsed">
            <Grid.RowDefinitions>
                <RowDefinition Height="*"/>
                <RowDefinition Height="Auto"/>
            </Grid.RowDefinitions>

            <ScrollViewer Grid.Row="0\""""

new_settings_top = """        <Grid Grid.Row="1" x:Name="SettingsView" Visibility="Collapsed">
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="*"/>
            </Grid.RowDefinitions>

            <!-- Sub-tabs for Settings -->
            <Border Grid.Row="0"
                    Background="{StaticResource MantleBrush}"
                    Padding="12,8"
                    BorderBrush="{StaticResource Surface0Brush}"
                    BorderThickness="0,0,0,1">
                <StackPanel Orientation="Horizontal" VerticalAlignment="Center">
                    <RadioButton Content="General" IsChecked="True" GroupName="SettingsTabs" Click="SettingsSubTab_Click" Tag="General" Style="{StaticResource MemorySubTabStyle}"/>
                    <RadioButton Content="Memory" GroupName="SettingsTabs" Click="SettingsSubTab_Click" Tag="Memory" Style="{StaticResource MemorySubTabStyle}" Margin="4,0,0,0"/>
                    <RadioButton Content="Profile" GroupName="SettingsTabs" Click="SettingsSubTab_Click" Tag="Profile" Style="{StaticResource MemorySubTabStyle}" Margin="4,0,0,0"/>
                    <RadioButton Content="Logs" GroupName="SettingsTabs" Click="SettingsSubTab_Click" Tag="Logs" Style="{StaticResource MemorySubTabStyle}" Margin="4,0,0,0"/>
                </StackPanel>
            </Border>

            <Grid Grid.Row="1" x:Name="SettingsContentArea">
            <ScrollViewer x:Name="SettingsGeneralView"
"""

xaml = xaml.replace(old_settings_top, new_settings_top)

# Now find the end of the SettingsView. 
# SettingsView ends before deep dive briefing? No, Briefing is before SettingsView in code?
# Wait, Memory, Profile, Briefing, Logs, Settings.
# The original order in XAML: MemoryView, ProfileView, BriefingView, LogsView, SettingsView.
# We need to extract MemoryView, ProfileView, LogsView and move them inside SettingsContentArea?
# Or easier: just change their parent. But regex parsing XML is tricky. 
# Better yet: we don't need to physically move them in XAML! 
# Since they all occupy Grid.Row="1" of the main split pane, they overlap perfectly with SettingsView.
# If SettingsView is visible, and MemoryView is visible, they clash.
# But what if we just leave them where they are in XAML, and let `SettingsSubTab_Click` handle toggling their visibility?
# Yes! That avoids moving massive chunks of XAML.
# Wait, but the new sub-tabs are INSIDE SettingsView!
# So if SettingsView is visible, its Row 0 has the tabs, Row 1 has SettingsGeneralView.
# But MemoryView spans the *entire* Grid.Row="1" of the main window!
# It will overlap the sub-tabs!
# Ah! That's why they MUST be moved inside `SettingsContentArea`.

# Actually, the python script can match the grids by their x:Name with a simple stack-based parser.
def extract_element(text, tag_string):
    start_idx = text.find(tag_string)
    if start_idx == -1: return "", text
    
    # Simple stack to find matching end tag
    stack = 0
    end_idx = start_idx
    i = start_idx
    while i < len(text):
        if text.startswith("<Grid", i):
            stack += 1
        elif text.startswith("</Grid>", i):
            stack -= 1
            if stack == 0:
                end_idx = i + len("</Grid>")
                break
        i += 1
        
    element = text[start_idx:end_idx]
    # Remove it from text
    new_text = text[:start_idx] + text[end_idx:]
    return element, new_text

memory_view, xaml = extract_element(xaml, '<Grid Grid.Row="1" x:Name="MemoryView"')
profile_view, xaml = extract_element(xaml, '<Grid Grid.Row="1" x:Name="ProfileView"')
logs_view, xaml = extract_element(xaml, '<Grid Grid.Row="1" x:Name="LogsView" Visibility="Collapsed" Background="{StaticResource BaseBrush}">')

# Remove Grid.Row="1" from the extracted views because inside SettingsContentArea they don't need it.
memory_view = memory_view.replace('<Grid Grid.Row="1" x:Name="MemoryView"', '<Grid x:Name="MemoryView"')
profile_view = profile_view.replace('<Grid Grid.Row="1" x:Name="ProfileView"', '<Grid x:Name="ProfileView"')
logs_view = logs_view.replace('<Grid Grid.Row="1" x:Name="LogsView"', '<Grid x:Name="LogsView"')

# Now SettingsContentArea needs to hold SettingsGeneralView, plus these.
# SettingsView ends with:
#             </ScrollViewer>
#         </Grid>
# So we need to insert the closing tag for SettingsContentArea.
# Wait, SettingsView is the last element. We can find the end of SettingsGeneralView (the ScrollViewer).
# But there's a footer in SettingsView?
# Let's check XAML.
# Actually, the simplest string replacement for the end of SettingsView is:
end_str = "            </ScrollViewer>\n        </Grid>"
new_end_str = f"            </ScrollViewer>\n{memory_view}\n{profile_view}\n{logs_view}\n            </Grid>\n        </Grid>"

# Replace the last occurrence of end_str
last_idx = xaml.rfind(end_str)
if last_idx != -1:
    xaml = xaml[:last_idx] + new_end_str + xaml[last_idx + len(end_str):]
else:
    print("Could not find end of SettingsView")

# Remove remaining whitespace/comments left behind where the views used to be
xaml = re.sub(r'<!-- ═══════════════════════════════════════════════════════════\s*MEMORY BROWSER.*?═══════════════════════════════════════════════════════ -->', '', xaml, flags=re.DOTALL)
xaml = re.sub(r'<!-- ═══════════════════════════════════════════════════════════\s*PROFILE / NUGGETS BROWSER.*?═══════════════════════════════════════════════════════ -->', '', xaml, flags=re.DOTALL)
xaml = re.sub(r'<!-- ═══════════════════════════════════════════════════════════\s*LOGS TAB.*?═══════════════════════════════════════════════════════ -->', '', xaml, flags=re.DOTALL)


with open(xaml_path, 'w', encoding='utf-8') as f:
    f.write(xaml)

print('Success XAML refactored')
