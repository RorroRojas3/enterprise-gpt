import { Permissions } from './Permissions';

export interface UserDto {
  id: string;
  firstName: string;
  lastName: string;
  email: string;
  fullName: string;
  permissions: Permissions[];
}
