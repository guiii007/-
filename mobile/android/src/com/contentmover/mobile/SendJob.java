package com.contentmover.mobile;
import android.app.job.*;
public class SendJob extends JobService {
  volatile boolean stopped;
  @Override public boolean onStartJob(JobParameters params){stopped=false;new Thread(()->{boolean retry=false;try{Transfer.sendPending(this);retry=Transfer.pending(this).length>0;}catch(Exception e){retry=true;}if(!stopped)jobFinished(params,retry);}).start();return true;}
  @Override public boolean onStopJob(JobParameters params){stopped=true;return true;}
}
