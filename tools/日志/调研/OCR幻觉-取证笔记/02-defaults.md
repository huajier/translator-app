# Defaults (first-hand, from source / runtime output)
PaddleOCR 2.x tools/infer/utility.py:
  --det_db_thresh 0.3 ; --det_db_box_thresh 0.6 ; --det_db_unclip_ratio 1.5
  --drop_score (default 0.5 per predict_system usage) 
  https://github.com/PaddlePaddle/PaddleOCR/blob/release/2.7/tools/infer/utility.py
PaddleOCR 2.7 ppocr/postprocess/rec_postprocess.py:
  def get_ignored_tokens(self): return [0]  # for ctc blank
  decode(): 'if int(text_index[...]) == int(end_idx): break'  end_idx==0 -> break at FIRST blank
  => text after first CTC blank is silently truncated (explains "extra chars" family of bugs)
PaddleX pipelines/OCR.yaml (release/3.0 and develop):
  TextDetection: thresh 0.3 / box_thresh 0.6 / unclip_ratio 1.5 / limit_side_len 64 / limit_type min
  TextRecognition: score_thresh: 0.0   <-- 3.x default = NO rec-score filtering at all
PaddleX ocr/pipeline.py: self.text_rec_score_thresh = text_rec_config.get("score_thresh", 0)
  filter: if rec_res["rec_score"] >= text_rec_score_thresh: keep
RapidOCR (python/rapidocr/config.yaml): Global.text_score: 0.5 ; min_side_len 30 ; max_side_len 2000
RapidOCR ch_ppocr_rec/utils.py decode():
  conf_list from text_prob over selection (blanks & dups removed)
  if len(conf_list)==0: conf_list=[0]     <-- all-blank line => score 0.0
  text = "".join(selected chars); score = mean(conf)
RapidOCR main.py filter_by_text_score(): if score < self.text_score: continue
=> an all-blank crop yields ("", 0.0) and IS dropped by score. A "7" that survives score
   filtering is NOT an all-blank decode; if it disappears when score filtering is on, its
   rec score is simply below threshold.
