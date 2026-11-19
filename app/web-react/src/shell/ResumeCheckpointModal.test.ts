import { fireEvent, render, screen } from '@testing-library/react';
import { createElement } from 'react';
import { expect, it, vi } from 'vitest';
import { ResumeCheckpointModal } from './ResumeCheckpointModal';

it('omits the command and leaves file and folder classification to the broker', () => {
  const onConfirm = vi.fn();
  const props = { chefKeys: [], submitting: false, error: null, onCancel: vi.fn(), onConfirm, onNavigate: vi.fn() };
  const { container, rerender, unmount } = render(createElement(ResumeCheckpointModal, props));
  const input = screen.getByRole('textbox');
  const submit = screen.getByRole('button', { name: 'Resume Bake' });
  expect(container.querySelector('code')).toBeNull();
  expect((submit as HTMLButtonElement).disabled).toBe(true);

  for (const checkpointPath of ['C:\\Fixture\\folder.json', 'C:\\Fixture\\checkpoint.json']) {
    fireEvent.change(input, { target: { value: checkpointPath } });
    expect(container.querySelector('code')).toBeNull();
    fireEvent.click(submit);
    expect(onConfirm).toHaveBeenLastCalledWith({ checkpointPath, force: false, chefKeyId: null });
  }

  fireEvent.change(input, { target: { value: 'C:\\Fixture\\missing.json' } });
  rerender(createElement(ResumeCheckpointModal, { ...props, error: 'The checkpoint path is unavailable.' }));
  expect(screen.getByRole('alert').textContent).toContain('checkpoint path is unavailable');
  expect(container.querySelector('code')).toBeNull();
  expect(onConfirm).toHaveBeenCalledTimes(2);
  unmount();
});