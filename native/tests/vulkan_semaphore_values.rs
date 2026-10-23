use ash::vk::{self, Handle};
use core::mem::MaybeUninit;
use semaphore_list::{SemaphoreList, SemaphoreListMode, SemaphoreType};

fn check_submission(timeline_wait: bool, timeline_signal: bool) {
    let mut wait = SemaphoreList::new(SemaphoreListMode::Wait);
    let mut signal = SemaphoreList::new(SemaphoreListMode::Signal);
    let wait_handle = vk::Semaphore::from_raw(1);
    let signal_handle = vk::Semaphore::from_raw(2);
    wait.push_wait(
        if timeline_wait { SemaphoreType::Timeline(wait_handle, 41) }
        else { SemaphoreType::Binary(wait_handle) },
        vk::PipelineStageFlags::TOP_OF_PIPE,
    );
    signal.push_signal(
        if timeline_signal { SemaphoreType::Timeline(signal_handle, 42) }
        else { SemaphoreType::Binary(signal_handle) },
    );
    let mut timeline = MaybeUninit::uninit();
    let submit = SemaphoreList::add_to_submit(&mut wait, &mut signal, vk::SubmitInfo::default(), &mut timeline);
    if !timeline_wait && !timeline_signal {
        assert!(submit.p_next.is_null(), "binary-only submissions need no timeline metadata");
        return;
    }
    assert!(!submit.p_next.is_null());
    // The live submit owns references to both lists and to this extension.
    let timeline = unsafe { &*submit.p_next.cast::<vk::TimelineSemaphoreSubmitInfo<'_>>() };
    assert_eq!(timeline.wait_semaphore_value_count, submit.wait_semaphore_count);
    assert_eq!(timeline.signal_semaphore_value_count, submit.signal_semaphore_count);
    assert!(!timeline.p_wait_semaphore_values.is_null());
    assert!(!timeline.p_signal_semaphore_values.is_null());
    if timeline_wait { assert_eq!(unsafe { *timeline.p_wait_semaphore_values }, 41); }
    if timeline_signal { assert_eq!(unsafe { *timeline.p_signal_semaphore_values }, 42); }
}

#[test] fn binary_wait_with_timeline_signal_has_complete_values() { check_submission(false, true); }
#[test] fn timeline_wait_with_binary_signal_has_complete_values() { check_submission(true, false); }
#[test] fn timeline_values_are_preserved() { check_submission(true, true); }
#[test] fn binary_only_submissions_have_no_timeline_extension() { check_submission(false, false); }
