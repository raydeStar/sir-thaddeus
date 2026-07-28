import { createFileRoute } from '@tanstack/react-router';
import { ConversationLibrary } from '../components/ConversationLibrary';

export const Route = createFileRoute('/chat/')({
  component: ConversationLibrary,
});
