import { PermissionDto } from './PermissionDto';

export interface UserDto {
  id: string;
  firstName: string;
  lastName: string;
  email: string;
  fullName: string;
  permissions: PermissionDto[];
}
