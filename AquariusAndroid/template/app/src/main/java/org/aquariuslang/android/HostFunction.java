package org.aquariuslang.android;

import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;

/** Only annotated capability methods are visible through JNI. */
@Retention(RetentionPolicy.RUNTIME)
@interface HostFunction {}
