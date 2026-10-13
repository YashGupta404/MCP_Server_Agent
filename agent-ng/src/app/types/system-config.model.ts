// One ECM system configuration offered in the "Choose your content system" picker.
export interface SystemConfig {
  friendlyName: string;
  systemType: string;
  description?: string;
  isDefault?: boolean;
  isActive?: boolean;
}
